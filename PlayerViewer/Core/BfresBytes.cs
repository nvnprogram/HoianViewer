using System.IO;
using System.Linq;
using BfresLibrary;

namespace PlayerViewer.Core
{
    /// <summary>A bfres read from and written back to bytes.</summary>
    public static class BfresBytes
    {
        public static ResFile Read(byte[] bytes) => new(new MemoryStream(bytes));

        /// <summary>The file's first model, the one a standalone model is.</summary>
        public static Model FirstModel(byte[] bytes) => Read(bytes).Models.Values.First();

        public static byte[] ToBytes(ResFile res)
        {
            var ms = new MemoryStream();
            res.Save(ms);
            return ms.ToArray();
        }
    }
}
