using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace LidarWalk
{
    [Serializable] public sealed class PointTile
    {
        public string file;
        public int count;
        public float[] boundsMin, boundsMax;
    }

    [StructLayout(LayoutKind.Sequential)] struct CompactPoint
    {
        public Vector3 position;
        public uint rgba;
    }

    // Disk reads run on two workers. Unity buffers are created and released only
    // on the main thread, with bounded residency and uploads per frame.
    public sealed class PointTileStreamer : IDisposable
    {
        sealed class TileState
        {
            public PointTile Tile;
            public string Path;
            public Bounds Bounds;
            public int Wanted, ResidentCount, FieldGeneration, LastUsed;
            public float Distance;
            public bool Loading, Failed;
            public ComputeBuffer Points, Values;
            public readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
        }

        sealed class Payload
        {
            public CompactPoint[] Points;
            public float[] Values;
        }

        sealed class Request
        {
            public TileState State;
            public int Generation, Count;
            public bool ReuseGeometry;
            public Task<Payload> Task;
        }

        readonly List<TileState> tiles = new List<TileState>();
        readonly List<TileState> visible = new List<TileState>();
        readonly List<Request> pending = new List<Request>();
        readonly Plane[] planes = new Plane[6];
        readonly Material material;
        readonly int fieldCount;
        readonly ComputeBuffer legacyPlaceholder;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        int fieldIndex = -1, generation;
        float nextRefresh;
        bool disposed;

        public int PointBudget = 3000000;
        public int MemoryBudgetMB = 256;
        public int UploadBudgetMB = 4;
        public float NearDistance = 20, ViewDistance = 300;
        public int RenderedPoints { get; private set; }
        public int TargetPoints { get; private set; }
        public int ResidentPoints { get; private set; }
        public long ResidentBytes { get; private set; }
        public int VisibleTiles => visible.Count;
        public int PendingLoads => pending.Count;
        public string Error { get; private set; }
        long MemoryLimit => Math.Max(16, MemoryBudgetMB) * 1024L * 1024L;

        public PointTileStreamer(string folder, SceneManifest manifest, Material material)
        {
            this.material = material;
            fieldCount = manifest.displayFields?.Length ?? 0;
            if (manifest.tiles == null || manifest.tiles.Length == 0 || fieldCount == 0)
                throw new InvalidDataException("Streaming scene has no tiles or fields.");
            string root = System.IO.Path.GetFullPath(folder) + System.IO.Path.DirectorySeparatorChar;
            long total = 0;
            foreach (var tile in manifest.tiles)
            {
                if (tile.count < 1 || tile.count > 1000000 || tile.boundsMin?.Length != 3 || tile.boundsMax?.Length != 3)
                    throw new InvalidDataException("Invalid streaming tile metadata.");
                for (int axis = 0; axis < 3; axis++)
                    if (float.IsNaN(tile.boundsMin[axis]) || float.IsInfinity(tile.boundsMin[axis]) ||
                        float.IsNaN(tile.boundsMax[axis]) || float.IsInfinity(tile.boundsMax[axis]) || tile.boundsMin[axis] > tile.boundsMax[axis])
                        throw new InvalidDataException("Invalid tile bounds.");
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, tile.file));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Tile path leaves the scene folder.");
                var bounds = new Bounds();
                bounds.SetMinMax(new Vector3(tile.boundsMin[0], tile.boundsMin[1], tile.boundsMin[2]),
                    new Vector3(tile.boundsMax[0], tile.boundsMax[1], tile.boundsMax[2]));
                bounds.Expand(1);
                tiles.Add(new TileState { Tile = tile, Path = path, Bounds = bounds });
                total += tile.count;
            }
            if (total != manifest.displayPointCount) throw new InvalidDataException("Tile counts do not match the scene manifest.");
            legacyPlaceholder = new ComputeBuffer(1, 32);
            material.SetBuffer("_Points", legacyPlaceholder);
            material.SetInt("_Streamed", 1);
        }

        public void SelectField(int index)
        {
            if (index < -1 || index >= fieldCount) throw new ArgumentOutOfRangeException(nameof(index));
            fieldIndex = index;
            generation++;
            cancellation.Cancel();
            cancellation.Dispose();
            cancellation = new CancellationTokenSource();
            nextRefresh = 0;
            Error = null;
            foreach (var tile in tiles)
            {
                tile.Failed = false;
                // RGB does not read the scalar buffer. Existing geometry is ready immediately.
                if (index == -1) tile.FieldGeneration = generation;
            }
        }

        public void Tick(Camera camera, int layer)
        {
            if (disposed || !camera) return;
            if (Time.unscaledTime >= nextRefresh)
            {
                RefreshVisibility(camera);
                nextRefresh = Time.unscaledTime + .15f;
            }
            CompleteRequests();
            MakeRoom(0, null);
            foreach (var tile in visible)
            {
                if (pending.Count >= 2) break;
                if (tile.Loading || tile.Failed || tile.Wanted == 0) continue;
                if (tile.ResidentCount >= tile.Wanted && tile.FieldGeneration == generation) continue;
                int count = tile.ResidentCount >= tile.Wanted ? tile.ResidentCount :
                    Math.Min(tile.Tile.count, ((tile.Wanted + 1023) / 1024) * 1024);
                bool reuse = tile.Points != null && tile.ResidentCount == count;
                int selected = fieldIndex;
                var token = cancellation.Token;
                var task = Task.Run(() => ReadTile(tile.Path, tile.Tile.count, count, fieldCount, selected, reuse, token), token);
                // Observe exceptions even if the application exits before the next Tick.
                task.ContinueWith(failed => { var observed = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                pending.Add(new Request { State = tile, Generation = generation, Count = count, ReuseGeometry = reuse, Task = task });
                tile.Loading = true;
            }
            RenderedPoints = 0;
            foreach (var tile in visible)
            {
                if (tile.Points == null || tile.FieldGeneration != generation) continue;
                int count = Math.Min(tile.Wanted, tile.ResidentCount);
                if (count == 0) continue;
                tile.LastUsed = Time.frameCount;
                Graphics.DrawProcedural(material, tile.Bounds, MeshTopology.Triangles, 6, count, camera,
                    tile.Properties, ShadowCastingMode.Off, false, layer);
                RenderedPoints += count;
            }
        }

        void RefreshVisibility(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            visible.Clear();
            double desired = 0;
            float near = Mathf.Max(1, NearDistance);
            float far = Mathf.Max(near, ViewDistance);
            foreach (var tile in tiles)
            {
                tile.Wanted = 0;
                tile.Distance = Mathf.Sqrt(tile.Bounds.SqrDistance(camera.transform.position));
                if (tile.Distance > far || !GeometryUtility.TestPlanesAABB(planes, tile.Bounds)) continue;
                float fraction = Mathf.Min(1, near * near / Mathf.Max(near * near, tile.Distance * tile.Distance));
                tile.Wanted = Math.Max(1, Mathf.CeilToInt(tile.Tile.count * fraction));
                visible.Add(tile);
                desired += tile.Wanted;
            }
            // Reserve cache space for page rounding, in-flight uploads and nearby tiles.
            double budget = Math.Min(Math.Max(1, PointBudget), MemoryLimit / 32);
            if (desired > budget)
                foreach (var tile in visible) tile.Wanted = (int)Math.Floor(tile.Wanted * budget / desired);
            TargetPoints = 0;
            foreach (var tile in visible) TargetPoints += tile.Wanted;
            visible.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        void CompleteRequests()
        {
            long uploaded = 0;
            long limit = Math.Max(1, UploadBudgetMB) * 1024L * 1024L;
            for (int i = 0; i < pending.Count;)
            {
                var request = pending[i];
                if (!request.Task.IsCompleted) { i++; continue; }
                long bytes = request.Count * (request.ReuseGeometry ? 4L : 20L);
                if (request.Generation == generation && !request.Task.IsCanceled && !request.Task.IsFaulted && uploaded > 0 && uploaded + bytes > limit)
                { i++; continue; }
                pending.RemoveAt(i);
                var tile = request.State;
                tile.Loading = false;
                if (request.Task.IsFaulted)
                {
                    var exception = request.Task.Exception.GetBaseException();
                    if (request.Generation == generation) Fail(tile, exception);
                    continue;
                }
                if (request.Task.IsCanceled || request.Generation != generation) continue;
                try
                {
                    Upload(request, request.Task.Result);
                    uploaded += bytes;
                }
                catch (Exception exception) { Fail(tile, exception); }
            }
        }

        void Upload(Request request, Payload payload)
        {
            var tile = request.State;
            if (request.ReuseGeometry)
            {
                tile.Values.SetData(payload.Values);
                tile.FieldGeneration = generation;
                return;
            }
            long bytes = request.Count * 20L;
            if (!MakeRoom(bytes, tile)) return;
            ComputeBuffer points = null, values = null;
            try
            {
                points = new ComputeBuffer(request.Count, 16);
                values = new ComputeBuffer(request.Count, 4);
                points.SetData(payload.Points);
                values.SetData(payload.Values);
                Release(tile);
                tile.Points = points; tile.Values = values;
                points = null; values = null;
                tile.ResidentCount = request.Count;
                tile.FieldGeneration = generation;
                tile.LastUsed = Time.frameCount;
                tile.Properties.SetBuffer("_TilePoints", tile.Points);
                tile.Properties.SetBuffer("_DisplayValues", tile.Values);
                tile.Properties.SetInt("_PointOffset", 0);
                ResidentBytes += bytes;
                ResidentPoints += request.Count;
            }
            finally { points?.Release(); values?.Release(); }
        }

        bool MakeRoom(long additionalBytes, TileState protectedTile)
        {
            if (ResidentBytes + additionalBytes <= MemoryLimit) return true;
            var candidates = tiles.FindAll(tile => tile.Points != null && tile != protectedTile && !tile.Loading);
            candidates.Sort((a, b) =>
            {
                int priority = (a.Wanted > 0).CompareTo(b.Wanted > 0);
                if (priority != 0) return priority;
                priority = a.LastUsed.CompareTo(b.LastUsed);
                return priority != 0 ? priority : b.Distance.CompareTo(a.Distance);
            });
            foreach (var tile in candidates)
            {
                Release(tile);
                if (ResidentBytes + additionalBytes <= MemoryLimit) return true;
            }
            return ResidentBytes + additionalBytes <= MemoryLimit;
        }

        void Release(TileState tile)
        {
            if (tile.Points == null) return;
            tile.Points.Release(); tile.Values.Release();
            tile.Points = null; tile.Values = null;
            ResidentBytes -= tile.ResidentCount * 20L;
            ResidentPoints -= tile.ResidentCount;
            tile.ResidentCount = 0;
        }

        void Fail(TileState tile, Exception exception)
        {
            tile.Failed = true;
            Error = $"Could not load {tile.Tile.file}: {exception.Message}";
            Debug.LogError(Error);
        }

        static Payload ReadTile(string path, int totalCount, int count, int fields, int selected, bool reuse, CancellationToken token)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != "LT01" || reader.ReadInt32() != totalCount || reader.ReadInt32() != fields ||
                stream.Length != 12L + totalCount * (16L + 4L * fields)) throw new InvalidDataException("Invalid streaming tile file.");
            var payload = new Payload();
            if (!reuse)
            {
                var bytes = ReadBytes(stream, count * 16, token);
                payload.Points = new CompactPoint[count];
                var pin = GCHandle.Alloc(payload.Points, GCHandleType.Pinned);
                try { Marshal.Copy(bytes, 0, pin.AddrOfPinnedObject(), bytes.Length); }
                finally { pin.Free(); }
            }
            payload.Values = new float[count];
            if (selected >= 0)
            {
                stream.Position = 12L + totalCount * 16L + selected * (long)totalCount * 4L;
                var bytes = ReadBytes(stream, count * 4, token);
                Buffer.BlockCopy(bytes, 0, payload.Values, 0, bytes.Length);
            }
            token.ThrowIfCancellationRequested();
            return payload;
        }

        static byte[] ReadBytes(Stream stream, int length, CancellationToken token)
        {
            var bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, offset, Math.Min(1024 * 1024, length - offset));
                if (read == 0) throw new EndOfStreamException("Truncated streaming tile.");
                offset += read;
            }
            return bytes;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            foreach (var tile in tiles) Release(tile);
            legacyPlaceholder.Release();
            cancellation.Dispose();
            pending.Clear();
        }
    }
}
