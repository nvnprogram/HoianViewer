using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ShaderLibrary
{
    public class GlslUtility
    {
        private static readonly Regex IncludeRegex = new Regex(@"#include\s+""(.+?)""", RegexOptions.Compiled);

        /// <summary>
        /// Gets other shader sources when paths are marked as #include.
        /// </summary> 
        /// <param name="shaderSource"></param>
        /// <param name="directory"></param>
        /// <returns></returns>
        /// <exception cref="FileNotFoundException"></exception>
        public static string ProcessIncludes(string shaderSource, string directory)
        {
            StringBuilder processedShader = new StringBuilder();

            foreach (string line in shaderSource.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                Match match = IncludeRegex.Match(line);
                if (match.Success)
                {
                    string includeFile = match.Groups[1].Value;
                    string includePath = Path.Combine(directory, includeFile);

                    if (File.Exists(includePath))
                    {
                        string includedSource = File.ReadAllText(includePath);
                        processedShader.Append(ProcessIncludes(includedSource, directory));
                    }
                    else
                    {
                        throw new FileNotFoundException($"Included file not found: {includePath}");
                    }
                }
                else
                {
                    processedShader.AppendLine(line);
                }
            }

            return processedShader.ToString();
        }

        /// <summary>
        /// Applies shader macros for a given shader source.
        /// </summary>
        /// <param name="macros"></param>
        /// <param name="shaderSource"></param>
        /// <returns></returns>
        public static string ApplyMacros(Dictionary<string, string> macros, string shaderSource)
        {
            if (string.IsNullOrEmpty(shaderSource))
                return shaderSource;

            var sb = new System.Text.StringBuilder();
            using (var writer = new System.IO.StringWriter(sb))
            {
                string[] stringSeparators = new string[] { "\r\n" };
                string[] lines = shaderSource.Split(stringSeparators, StringSplitOptions.None);

                foreach (var line in lines)
                {
                    // Start of macro
                    if (!line.StartsWith("#define"))
                    {
                        writer.WriteLine(line);
                        continue;
                    }

                    // split to macro data
                    var macroName = line.Split()[1];
                    // Check if macro name is present
                    if (!macros.ContainsKey(macroName))
                    {
                        writer.WriteLine(line);
                        continue;
                    }

                    // Macro value ie #define skin_count 1
                    var macroValue = line.Split()[2];

                    var macroNew = macros[macroName];

                    // Boolean types as macro inputs expect 0 or 1 as values
                    bool isBool = macroValue.Contains("true") || macroValue.Contains("false");

                    if (isBool) // Set as true or false if necessary
                    {
                        if (macroNew == "1") macroNew = "true";
                        if (macroNew == "0") macroNew = "false";
                    }

                    Console.WriteLine($"{macroValue} -> {macroNew}");

                    // Updated macro value in shader code
                    writer.WriteLine(string.Format("#define {0} {1}", macroName, macroNew));
                }
            }
            return sb.ToString();
        }
    }
}
