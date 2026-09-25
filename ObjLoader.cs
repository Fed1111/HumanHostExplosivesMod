using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace HumanHostExplosives
{
    /// <summary>
    /// Minimal runtime .obj loader. Assumes a triangulated mesh with per-corner
    /// position/uv(/normal) indices. Converts from OBJ's right-handed coordinate space to
    /// Unity's left-handed space by negating X and reversing triangle winding.
    ///
    /// Two modes:
    ///  - useFileNormals = false (grenade, nailbomb - the published look): one vertex per face
    ///    corner and RecalculateNormals, i.e. flat shading. Their Meshy source had no usable
    ///    normals and the faceted look is what players have seen since 0.1.
    ///  - useFileNormals = true (the 0.3.0 Blender models): read `vn`, weld identical
    ///    (v, vt, vn) corners so smooth shading survives, and keep the file's normals. Without
    ///    this a 48-segment bottle renders as visible flat strips.
    /// </summary>
    internal static class ObjLoader
    {
        private static readonly char[] Whitespace = { ' ', '\t' };

        public static Mesh LoadMesh(string objPath)
        {
            return LoadMesh(objPath, useFileNormals: false);
        }

        public static Mesh LoadMesh(string objPath, bool useFileNormals)
        {
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var finalVerts = new List<Vector3>();
            var finalUvs = new List<Vector2>();
            var finalNormals = new List<Vector3>();
            var triangles = new List<int>();
            var welded = new Dictionary<string, int>();

            foreach (string rawLine in File.ReadLines(objPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] parts = line.Split(Whitespace, System.StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "v":
                        positions.Add(new Vector3(-Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                        break;
                    case "vt":
                        uvs.Add(new Vector2(Parse(parts[1]), Parse(parts[2])));
                        break;
                    case "vn":
                        normals.Add(new Vector3(-Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                        break;
                    case "f":
                        int a = AddCorner(parts[1], useFileNormals, positions, uvs, normals, finalVerts, finalUvs, finalNormals, welded);
                        int b = AddCorner(parts[2], useFileNormals, positions, uvs, normals, finalVerts, finalUvs, finalNormals, welded);
                        int c = AddCorner(parts[3], useFileNormals, positions, uvs, normals, finalVerts, finalUvs, finalNormals, welded);

                        // Reversed winding (a, c, b) to match the X negation above.
                        triangles.Add(a);
                        triangles.Add(c);
                        triangles.Add(b);
                        break;
                }
            }

            var mesh = new Mesh
            {
                indexFormat = finalVerts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(finalVerts);
            mesh.SetUVs(0, finalUvs);
            mesh.SetTriangles(triangles, 0);
            if (useFileNormals && normals.Count > 0)
            {
                mesh.SetNormals(finalNormals);
            }
            else
            {
                mesh.RecalculateNormals();
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        private static float Parse(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static int AddCorner(
            string token,
            bool weld,
            List<Vector3> positions,
            List<Vector2> uvs,
            List<Vector3> normals,
            List<Vector3> finalVerts,
            List<Vector2> finalUvs,
            List<Vector3> finalNormals,
            Dictionary<string, int> welded)
        {
            if (weld && welded.TryGetValue(token, out int existing))
            {
                return existing;
            }

            string[] parts = token.Split('/');
            int posIdx = int.Parse(parts[0], CultureInfo.InvariantCulture) - 1;
            int uvIdx = parts.Length > 1 && parts[1].Length > 0
                ? int.Parse(parts[1], CultureInfo.InvariantCulture) - 1
                : -1;
            int nIdx = parts.Length > 2 && parts[2].Length > 0
                ? int.Parse(parts[2], CultureInfo.InvariantCulture) - 1
                : -1;

            finalVerts.Add(positions[posIdx]);
            finalUvs.Add(uvIdx >= 0 ? uvs[uvIdx] : Vector2.zero);
            finalNormals.Add(nIdx >= 0 && nIdx < normals.Count ? normals[nIdx] : Vector3.up);
            int index = finalVerts.Count - 1;
            if (weld)
            {
                welded[token] = index;
            }
            return index;
        }
    }
}
