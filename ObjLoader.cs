using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace HumanHostExplosives
{
    /// <summary>
    /// Minimal runtime .obj loader. Assumes a triangulated mesh with per-corner
    /// position/uv indices (no shared-vertex optimization needed at this poly count).
    /// Converts from OBJ's right-handed coordinate space to Unity's left-handed space
    /// by negating X and reversing triangle winding.
    /// </summary>
    internal static class ObjLoader
    {
        public static Mesh LoadMesh(string objPath)
        {
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var finalVerts = new List<Vector3>();
            var finalUvs = new List<Vector2>();
            var triangles = new List<int>();

            foreach (string rawLine in File.ReadLines(objPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line.StartsWith("v "))
                {
                    string[] parts = line.Split(' ');
                    float x = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    float y = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    float z = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    positions.Add(new Vector3(-x, y, z));
                }
                else if (line.StartsWith("vt "))
                {
                    string[] parts = line.Split(' ');
                    float u = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    float v = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    uvs.Add(new Vector2(u, v));
                }
                else if (line.StartsWith("f "))
                {
                    string[] tokens = line.Split(' ');
                    int a = AddCorner(tokens[1], positions, uvs, finalVerts, finalUvs);
                    int b = AddCorner(tokens[2], positions, uvs, finalVerts, finalUvs);
                    int c = AddCorner(tokens[3], positions, uvs, finalVerts, finalUvs);

                    // Reversed winding (a, c, b) to match the X negation above.
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                }
            }

            var mesh = new Mesh
            {
                indexFormat = finalVerts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(finalVerts);
            mesh.SetUVs(0, finalUvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        private static int AddCorner(
            string token,
            List<Vector3> positions,
            List<Vector2> uvs,
            List<Vector3> finalVerts,
            List<Vector2> finalUvs)
        {
            string[] parts = token.Split('/');
            int posIdx = int.Parse(parts[0], CultureInfo.InvariantCulture) - 1;
            int uvIdx = parts.Length > 1 && parts[1].Length > 0
                ? int.Parse(parts[1], CultureInfo.InvariantCulture) - 1
                : -1;

            finalVerts.Add(positions[posIdx]);
            finalUvs.Add(uvIdx >= 0 ? uvs[uvIdx] : Vector2.zero);
            return finalVerts.Count - 1;
        }
    }
}
