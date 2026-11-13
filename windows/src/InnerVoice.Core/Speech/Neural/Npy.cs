using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace InnerVoice.Core.Speech.Neural;

/// A 2-D float32 NumPy array (.npy), mapped from disk rather than read into memory: Qwen3-TTS's
/// text embedding alone is 1.2 GB, and a line only needs a few dozen of its rows.
public sealed class NpyMatrix : IDisposable
{
    readonly MemoryMappedFile? file;
    readonly MemoryMappedViewAccessor? view;
    readonly float[]? data;
    readonly long offset;

    public int Rows { get; }
    public int Columns { get; }

    NpyMatrix(MemoryMappedFile? file, MemoryMappedViewAccessor? view, float[]? data, long offset, int rows, int columns)
    {
        this.file = file;
        this.view = view;
        this.data = data;
        this.offset = offset;
        Rows = rows;
        Columns = columns;
    }

    /// A small array in memory (tests, and the projection weights read whole).
    public static NpyMatrix InMemory(float[] data, int rows, int columns) => new(null, null, data, 0, rows, columns);

    public static NpyMatrix Open(string path, bool inMemory = false)
    {
        var (headerEnd, shape) = ReadHeader(path);
        int rows = shape.Length > 0 ? shape[0] : 1;
        int cols = shape.Length > 1 ? shape[1] : 1;
        if (shape.Length == 1) { rows = 1; cols = shape[0]; }
        if (inMemory)
        {
            var bytes = File.ReadAllBytes(path);
            var floats = new float[(long)rows * cols];
            Buffer.BlockCopy(bytes, (int)headerEnd, floats, 0, floats.Length * 4);
            return new NpyMatrix(null, null, floats, 0, rows, cols);
        }
        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return new NpyMatrix(mmf, view, null, headerEnd, rows, cols);
    }

    static (long HeaderEnd, int[] Shape) ReadHeader(string path)
    {
        using var f = File.OpenRead(path);
        var magic = new byte[8];
        f.ReadExactly(magic);
        if (magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY") throw new InvalidDataException($"{Path.GetFileName(path)} isn’t a NumPy file.");
        int major = magic[6];
        int length;
        if (major == 1) { var b = new byte[2]; f.ReadExactly(b); length = BitConverter.ToUInt16(b); }
        else { var b = new byte[4]; f.ReadExactly(b); length = (int)BitConverter.ToUInt32(b); }
        var header = new byte[length];
        f.ReadExactly(header);
        var text = Encoding.Latin1.GetString(header);
        if (!text.Contains("'<f4'")) throw new InvalidDataException($"{Path.GetFileName(path)} isn’t float32.");
        if (text.Contains("'fortran_order': True")) throw new InvalidDataException($"{Path.GetFileName(path)} is in Fortran order.");
        var shape = Regex.Match(text, @"'shape':\s*\(([^)]*)\)").Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        return (f.Position, shape);
    }

    /// Copies row `i` into `into` (Columns long).
    public void Row(int i, Span<float> into)
    {
        if ((uint)i >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(i));
        if (data is not null) { data.AsSpan(i * Columns, Columns).CopyTo(into); return; }
        var tmp = new float[Columns];
        view!.ReadArray(offset + (long)i * Columns * 4, tmp, 0, Columns);
        tmp.CopyTo(into);
    }

    public float[] Row(int i)
    {
        var r = new float[Columns];
        Row(i, r);
        return r;
    }

    /// Adds row `i` to `into`.
    public void AddRow(int i, Span<float> into)
    {
        Span<float> r = Columns <= 4096 ? stackalloc float[Columns] : new float[Columns];
        Row(i, r);
        for (int k = 0; k < Columns; k++) into[k] += r[k];
    }

    /// The whole array, row-major (only for small ones).
    public float[] All()
    {
        if (data is not null) return data;
        var all = new float[(long)Rows * Columns];
        view!.ReadArray(offset, all, 0, all.Length);
        return all;
    }

    public void Dispose()
    {
        view?.Dispose();
        file?.Dispose();
    }
}
