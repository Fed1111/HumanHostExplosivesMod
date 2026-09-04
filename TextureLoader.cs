using System.IO;
using UnityEngine;

namespace HumanHostExplosives
{
    internal static class TextureLoader
    {
        public static Texture2D LoadPng(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: false);
            tex.LoadImage(bytes);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return tex;
        }
    }
}
