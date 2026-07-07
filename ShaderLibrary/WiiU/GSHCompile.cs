using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.WiiU
{
    public class GSHCompile
    {
        public static string GSH_PATH = "gshCompile.exe";
        public static string OUTPUT_PATH = "temp.gsh";

        public GSHCompile()
        {

        }

        public static bool IsValid()
        {
            return File.Exists(GSH_PATH);
        }

        public static byte[] CompileStages(string vertex, string fragment, string geometry) 
        {
            string vsh_path = "temp.vert";
            string fsh_path = "temp.frag";
            string gsh_path = "temp.geom";

            if (File.Exists(OUTPUT_PATH)) File.Delete(OUTPUT_PATH);

            //save shader
            File.WriteAllText(vsh_path, vertex);
            File.WriteAllText(fsh_path, fragment);
            if (!string.IsNullOrEmpty(geometry))
                File.WriteAllText(gsh_path, geometry);

            string cmd = $"";
            // Shader stages
            if (!string.IsNullOrEmpty(vertex))    cmd += $" -v {vsh_path}";
            if (!string.IsNullOrEmpty(fragment))  cmd += $" -p {fsh_path}";
            if (!string.IsNullOrEmpty(geometry))  cmd += $" -g {gsh_path}";

            //  Exec(GSH_PATH, $"-v {vsh_path} -p {fsh_path} -o {OUTPUT_PATH} -force_uniformblock -no_limit_array_syms -nospark -O");
            Exec(GSH_PATH, $"{cmd} -o {OUTPUT_PATH} -force_uniformblock -no_limit_array_syms -nospark -O");

            if (File.Exists(OUTPUT_PATH))
            {
                return File.ReadAllBytes(OUTPUT_PATH);
            }
            return new byte[0]; //failed
        }

        static string GetTypeArg(GSHShaderType type)
        {
            switch (type)
            {
                case GSHShaderType.Vertex: return "-v";
                case GSHShaderType.Pixel: return "-p";
                case GSHShaderType.Geometry: return "-g";
                case GSHShaderType.Compute: return "-c";
                default:
                    throw new ArgumentException($"Invalid type argument {type}!");
            }
        }

        public enum GSHShaderType
        {
            Vertex,
            Pixel,
            Geometry,
            Compute,
        }

        private static bool Exec(string exec, string args)
        {
            Console.WriteLine(args);

            var info = new ProcessStartInfo
            {
                FileName = exec,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process cmd = new Process();
            cmd.StartInfo = info;
            cmd.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine(e.Data);
            };
            cmd.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine($"Error: {e.Data}");
            };
            cmd.Start();

            cmd.BeginOutputReadLine();
            cmd.BeginErrorReadLine();

            cmd.WaitForExit();

            return cmd.ExitCode == 0;
            /*
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/C " + $"{exec} {args}");
            info.CreateNoWindow = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            Process cmd = new Process();
            cmd.StartInfo = info;
            cmd.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    if (e.Data.Contains("error"))
                        Console.WriteLine($"Error: {e.Data}");
                }
            };
            cmd.Start();

            cmd.BeginOutputReadLine();
            cmd.BeginErrorReadLine();

            cmd.WaitForExit();

            return cmd.ExitCode == 0;*/
        }

        public static string CompileMacros(Dictionary<string, string> macros, string src)
        {
            var sb = new System.Text.StringBuilder();
            using (var writer = new System.IO.StringWriter(sb))
            {
                string[] stringSeparators = new string[] { "\r\n" };
                string[] lines = src.Split(stringSeparators, StringSplitOptions.None);

                foreach (var line in lines)
                {
                    string value = line;
                    if (line.StartsWith("#define"))
                    {
                        var macroName = line.Split()[1];
                        if (macros.ContainsKey(macroName))
                        {
                            var macro_values = line.Split();

                            var macroValue = macro_values[2];
                            bool isBool = macroValue.Contains("true") || macroValue.Contains("false");

                            if (isBool)
                            {
                                if (macros[macroName] == "1") macros[macroName] = "true";
                                if (macros[macroName] == "0") macros[macroName] = "false";
                            }

                            value = value.Replace(macroValue, macros[macroName]);
                        }
                    }
                    writer.WriteLine(value);
                }
            }
            return sb.ToString();
        }
    }
}
