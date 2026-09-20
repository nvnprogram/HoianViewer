using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ShaderBundler
{
    /// <summary>One specialisation: an ubershader stage, the option table it was built
    /// against, the bank that stage binds the option block to, and the option vector.</summary>
    public sealed class UberspecRequest
    {
        public ShaderBinary Uber;
        public int OptionBank = -1;
        public string OptionsTableJson;
        public string OptionsFile;

        /// <summary>Names the splice in error messages. Not passed to the tool.</summary>
        public string Label = "splice";

        /// <summary>
        /// Fast run without choosing optimal result or checking it
        /// </summary>
        public bool Quick;
    }

    public sealed class UberspecResult
    {
        public ShaderBinary Binary;

        /// <summary>What the gates said, as "N pass, M not run".</summary>
        public string GateSummary;
    }

    /// <summary>
    /// Runs the native specialiser and reads its two blobs back.
    /// </summary>
    public sealed class UberspecRunner
    {
        /// <summary>The gates the tool prints. One that is not printed is a failure, not a
        /// pass: a scraper that only counted FAIL lines once missed a gate for two whole
        /// builds.</summary>
        static readonly string[] ExpectedGates =
        {
            "V1",
            "V2",
            "V3",
            "V4",
            "V5",
            "V6",
            "V7",
            "V8",
            "V9",
            "V10",
            "V11",
            "V12",
            "V13",
            "V14",
        };

        static readonly Regex GateRe = new(
            @"^\s*(V\d+)\s+(.+?)\s+(PASS|FAIL)(?:\s+(.*))?$",
            RegexOptions.Compiled
        );

        //The only gate allowed to report NOT RUN: it needs a reference program this pipeline
        //never supplies.
        const string ReferenceGate = "V9";

        readonly string _executable;
        readonly string _workRoot;
        readonly int _timeoutMs;

        //The options table is the one input that does not change per splice, so it is written
        //once, named by its content so runners sharing a work root share the file.
        readonly object _tableGate = new();
        string _tableJson;
        string _tablePath;

        public UberspecRunner(string executablePath, string workRoot, int timeoutMs = 300_000)
        {
            _executable = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
            _workRoot = workRoot ?? throw new ArgumentNullException(nameof(workRoot));
            _timeoutMs = timeoutMs;
        }

        /// <summary>
        /// The skibidi slicer.
        /// </summary>
        public static string FindExecutable(string directory)
        {
            if (directory == null)
                throw new ArgumentNullException(nameof(directory));
            foreach (
                var name in new[] { "uberslicer.exe", "uberslicer", "uberspec.exe", "uberspec" }
            )
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        /// <summary>
        /// The codegen version the specialiser stamps into every control blob it writes, or
        /// <see cref="BundleStamp.Unstamped"/> when it does not answer.
        /// </summary>
        public static int QueryCodegen(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
                return BundleStamp.Unstamped;
            try
            {
                var psi = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("codegen");
                using var p = Process.Start(psi);
                if (p == null)
                    return BundleStamp.Unstamped;
                string so = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10000))
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch { }
                    return BundleStamp.Unstamped;
                }
                if (p.ExitCode != 0 || !int.TryParse(so.Trim(), out int version))
                    return BundleStamp.Unstamped;
                return version;
            }
            catch
            {
                return BundleStamp.Unstamped;
            }
        }

        /// <summary>Removes the per invocation directories a crash or a kill left behind.</summary>
        public static void SweepWorkRoot(string workRoot)
        {
            if (workRoot == null || !Directory.Exists(workRoot))
                return;
            foreach (var dir in Directory.GetDirectories(workRoot))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch { }
            }
        }

        string TablePath(string json)
        {
            lock (_tableGate)
            {
                if (_tablePath != null && _tableJson == json && File.Exists(_tablePath))
                    return _tablePath;
                Directory.CreateDirectory(_workRoot);
                string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json)));
                string path = Path.Combine(_workRoot, $"options_{hash[..16]}.json");
                if (!File.Exists(path))
                {
                    //Written beside and moved in, so a reader never sees a partial file and a
                    //second writer of the same table loses the race harmlessly.
                    string tmp = Path.Combine(_workRoot, $"{Guid.NewGuid():N}.tmp");
                    File.WriteAllText(tmp, json);
                    try
                    {
                        File.Move(tmp, path);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        File.Delete(tmp);
                    }
                }
                _tableJson = json;
                _tablePath = path;
                return path;
            }
        }

        public UberspecResult Generate(UberspecRequest req, CancellationToken cancel = default)
        {
            if (req?.Uber?.ByteCode == null || req.Uber.ByteCode.Length == 0)
                throw new ArgumentException("no ubershader byte code", nameof(req));
            if (req.Uber.ControlCode == null || req.Uber.ControlCode.Length == 0)
                throw new ArgumentException("no ubershader control code", nameof(req));
            if (req.OptionBank < 0)
                throw new ArgumentException(
                    $"{req.Label}: no option bank. It is per stage and per archive, so it has to come "
                        + "from the ubershader program; a wrong one yields a dead shader at exit code 0.",
                    nameof(req)
                );
            if (string.IsNullOrEmpty(req.OptionsTableJson))
                throw new ArgumentException("no options table", nameof(req));

            cancel.ThrowIfCancellationRequested();

            //A directory of its own per invocation, so parallel splices cannot collide. It is
            //removed on success and left in place, named in the error, on any failure.
            string dir = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var result = Run(req, dir, cancel);
            try
            {
                Directory.Delete(dir, true);
            }
            catch { }
            return result;
        }

        UberspecResult Run(UberspecRequest req, string dir, CancellationToken cancel)
        {
            string uber = Path.Combine(dir, "uber_bytecode.bin");
            string uberControl = Path.Combine(dir, "uber_control.bin");
            string table = TablePath(req.OptionsTableJson);
            string options = Path.Combine(dir, "options.txt");
            File.WriteAllBytes(uber, req.Uber.ByteCode);
            File.WriteAllBytes(uberControl, req.Uber.ControlCode);
            File.WriteAllText(options, req.OptionsFile ?? "");

            var psi = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir,
            };
            psi.ArgumentList.Add("--uber");
            psi.ArgumentList.Add(uber);
            psi.ArgumentList.Add("--uber-control");
            psi.ArgumentList.Add(uberControl);
            psi.ArgumentList.Add("--options-table");
            psi.ArgumentList.Add(table);
            psi.ArgumentList.Add("--option-bank");
            psi.ArgumentList.Add(req.OptionBank.ToString());
            psi.ArgumentList.Add("--options-file");
            psi.ArgumentList.Add(options);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("--name");
            psi.ArgumentList.Add("out");
            if (req.Quick)
                psi.ArgumentList.Add("--quick");
            psi.ArgumentList.Add("--gate");

            using var p =
                Process.Start(psi)
                ?? throw new InvalidOperationException($"could not start '{_executable}'");

            using var kill = cancel.Register(() =>
            {
                try
                {
                    p.Kill(true);
                }
                catch { }
            });

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(_timeoutMs))
            {
                try
                {
                    p.Kill(true);
                }
                catch { }
                throw new TimeoutException(
                    $"{req.Label}: specialiser timed out after {_timeoutMs} ms; its work "
                        + $"directory is kept at {dir}"
                );
            }
            cancel.ThrowIfCancellationRequested();

            string so = stdout.GetAwaiter().GetResult();
            string se = stderr.GetAwaiter().GetResult();

            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{req.Label}: specialiser exit {p.ExitCode} (work directory {dir})\n{so}\n{se}"
                );

            //A name the tool does not know is skipped with a warning and exit 0, and a
            //vector with such a name in it is not the vector that was asked for.
            foreach (var line in se.Split('\n'))
                if (line.Contains("no such option", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"{req.Label}: the specialiser ignored an option: {line.Trim()}"
                    );

            var gates = ReadGates(so);
            var failed = Named(gates, "FAIL");
            if (failed.Count > 0)
                throw new InvalidOperationException(
                    $"{req.Label}: {failed.Count} verification gate(s) failed: "
                        + string.Join(", ", failed)
                        + $" (work directory {dir})\n"
                        + so
                );

            var missing = Named(gates, "MISSING");
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"{req.Label}: the specialiser printed no line for gate(s) "
                        + string.Join(", ", missing)
                        + ", which is not a pass."
                );

            var notRun = Named(gates, "NOT RUN");
            notRun.Remove(ReferenceGate);
            if (notRun.Count > 0)
                throw new InvalidOperationException(
                    $"{req.Label}: gate(s) {string.Join(", ", notRun)} did not run, and only "
                        + $"{ReferenceGate} may be skipped."
                );

            string code = Path.Combine(dir, "out_bytecode.bin");
            string control = Path.Combine(dir, "out_control.bin");
            if (!File.Exists(code) || !File.Exists(control))
                throw new FileNotFoundException(
                    $"{req.Label}: the specialiser produced no out_bytecode.bin / out_control.bin "
                        + $"in {dir}"
                );

            int passed = Named(gates, "PASS").Count;
            int skipped = ExpectedGates.Length - passed;
            var binary = new ShaderBinary(File.ReadAllBytes(code), File.ReadAllBytes(control));

            if (BundleStamp.Codegen == BundleStamp.Unstamped)
                BundleStamp.Codegen = BundleStamp.Read(binary.ControlCode);

            return new UberspecResult
            {
                Binary = binary,
                GateSummary = skipped == 0 ? $"{passed} pass" : $"{passed} pass, {skipped} not run",
            };
        }

        //Gate name to verdict: PASS, FAIL, NOT RUN or MISSING.
        static Dictionary<string, string> ReadGates(string stdout)
        {
            var gates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (stdout ?? "").Split('\n'))
            {
                var m = GateRe.Match(line.TrimEnd('\r'));
                if (!m.Success)
                    continue;

                string verdict = m.Groups[3].Value;
                string detail = m.Groups[4].Value;
                if (verdict == "PASS" && detail.Contains("NOT RUN"))
                    verdict = "NOT RUN";
                gates[m.Groups[1].Value] = verdict;
            }
            foreach (var g in ExpectedGates)
                if (!gates.ContainsKey(g))
                    gates[g] = "MISSING";
            return gates;
        }

        static List<string> Named(Dictionary<string, string> gates, string verdict)
        {
            var names = new List<string>();
            foreach (var g in ExpectedGates)
                if (gates.TryGetValue(g, out var v) && v == verdict)
                    names.Add(g);
            return names;
        }
    }
}
