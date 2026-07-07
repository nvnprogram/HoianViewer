using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ShaderLibrary
{
    public class GLSLShaderLoader
    {
        public static string LoadShader(string filePath)
        {
            return GlslUtility.ProcessIncludes(File.ReadAllText(filePath), Path.GetDirectoryName(filePath));
        }
    }
}
