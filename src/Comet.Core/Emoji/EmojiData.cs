using System.Runtime.InteropServices;

namespace Comet.Core.Emoji;

/// <summary>
/// Read-only view over the compiled emoji dataset. When loaded from the embedded resource the
/// data is used in place (no copy, no parsing); dropping the instance releases everything.
/// </summary>
public sealed unsafe class EmojiData : IDisposable
{
    private const string ResourceName = "Comet.emoji.bin";

    private readonly byte* _base;
    private readonly bool _ownsMemory;
    private readonly int[] _sectionOffset = new int[(int)EmojiBlob.Section.Count];
    private readonly int[] _sectionCount = new int[(int)EmojiBlob.Section.Count];
    private readonly Stream? _keepAlive;
    private bool _disposed;

    private EmojiData(byte* basePtr, long length, bool ownsMemory, Stream? keepAlive)
    {
        _base = basePtr;
        _ownsMemory = ownsMemory;
        _keepAlive = keepAlive;
        try
        {
            ReadHeader(length);
        }
        catch
        {
            // The caller still owns the memory when construction fails; the finalizer must not free it too.
            GC.SuppressFinalize(this);
            throw;
        }
    }

    private void ReadHeader(long length)
    {
        var basePtr = _base;
        const int headerSize = EmojiBlob.HeaderFixedSize + ((int)EmojiBlob.Section.Count * 8);
        if (length < headerSize || *(uint*)basePtr != EmojiBlob.Magic)
            throw new InvalidDataException("Emoji data has an unexpected header.");
        FormatVersion = *(int*)(basePtr + 4);
        if (FormatVersion != EmojiBlob.FormatVersion)
            throw new InvalidDataException($"Emoji data format {FormatVersion} is not supported.");
        DataVersion = *(int*)(basePtr + 8);
        if (*(int*)(basePtr + 12) != (int)EmojiBlob.Section.Count)
            throw new InvalidDataException("Emoji data has an unexpected section count.");

        for (var i = 0; i < (int)EmojiBlob.Section.Count; i++)
        {
            var offset = *(int*)(basePtr + EmojiBlob.HeaderFixedSize + (i * 8));
            var count = *(int*)(basePtr + EmojiBlob.HeaderFixedSize + 4 + (i * 8));
            var size = (long)count * SectionElementSize((EmojiBlob.Section)i);
            if (offset < headerSize || count < 0 || offset + size > length)
                throw new InvalidDataException("Emoji data is truncated or corrupt.");
            _sectionOffset[i] = offset;
            _sectionCount[i] = count;
        }

        Count = _sectionCount[(int)EmojiBlob.Section.Emoji];
        Validate();
    }

    private static int SectionElementSize(EmojiBlob.Section s) => s switch
    {
        EmojiBlob.Section.Chars => sizeof(char),
        EmojiBlob.Section.Emoji => sizeof(EmojiRecord),
        EmojiBlob.Section.Terms => sizeof(TermRecord),
        EmojiBlob.Section.Postings => sizeof(uint),
        _ => sizeof(StringRef),
    };

    /// <summary>Bounds-checks every reference once, so a corrupt pack fails to load instead of failing mid-search.</summary>
    private void Validate()
    {
        var chars = Section<char>(EmojiBlob.Section.Chars).Length;
        bool Ok(StringRef r) => r.Offset >= 0 && r.Length >= 0 && (long)r.Offset + r.Length <= chars;
        bool Range(int first, int count, EmojiBlob.Section s) => first >= 0 && count >= 0 && (long)first + count <= _sectionCount[(int)s];

        foreach (var r in Records)
        {
            if (!Ok(r.Emoji) || !Ok(r.Name) || r.Emoji.Length == 0 || r.ShortcodeCount == 0
                || !Range(r.FirstShortcode, r.ShortcodeCount, EmojiBlob.Section.Shortcodes)
                || (r.FirstTone >= 0 && !Range(r.FirstTone, 5, EmojiBlob.Section.Tones)))
                throw new InvalidDataException("Emoji data has an invalid emoji record.");
        }

        ReadOnlySpan<EmojiBlob.Section> stringSections = [EmojiBlob.Section.Shortcodes, EmojiBlob.Section.Tones];
        foreach (var s in stringSections)
        {
            foreach (var r in Section<StringRef>(s))
            {
                if (!Ok(r))
                    throw new InvalidDataException("Emoji data has an invalid string reference.");
            }
        }

        foreach (var t in Terms)
        {
            if (!Ok(t.Text) || t.Text.Length == 0 || !Range(t.PostingStart, t.PostingCount, EmojiBlob.Section.Postings))
                throw new InvalidDataException("Emoji data has an invalid term.");
        }

        foreach (var p in Postings)
        {
            if (Posting.Emoji(p) >= Count)
                throw new InvalidDataException("Emoji data has an invalid posting.");
        }
    }

    /// <summary>Layout version of the blob (see <see cref="EmojiBlob.FormatVersion"/>).</summary>
    public int FormatVersion { get; private set; }

    /// <summary>Version of the data itself (yyyymmddNN); higher is newer.</summary>
    public int DataVersion { get; private set; }

    /// <summary>The blob format this build reads; packs with another format are ignored.</summary>
    public static int SupportedFormatVersion => EmojiBlob.FormatVersion;

    /// <summary>Reads only the header fields, without loading or validating the rest.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> blob, out int formatVersion, out int dataVersion)
    {
        formatVersion = dataVersion = 0;
        if (blob.Length < EmojiBlob.HeaderFixedSize || BitConverter.ToUInt32(blob) != EmojiBlob.Magic)
            return false;
        formatVersion = BitConverter.ToInt32(blob[4..]);
        dataVersion = BitConverter.ToInt32(blob[8..]);
        return true;
    }

    /// <summary>Number of emoji in the dataset.</summary>
    public int Count { get; private set; }

    /// <summary>Loads the dataset compiled into this assembly.</summary>
    public static EmojiData LoadEmbedded()
    {
        var stream = typeof(EmojiData).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        if (stream is UnmanagedMemoryStream ums)
            return new EmojiData(ums.PositionPointer, ums.Length, ownsMemory: false, keepAlive: ums);

        using (stream)
        {
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return FromBytes(bytes);
        }
    }

    /// <summary>Wraps a blob produced by <see cref="EmojiBlobWriter"/> (copied to native memory).</summary>
    public static EmojiData FromBytes(ReadOnlySpan<byte> bytes)
    {
        var mem = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(bytes.Length, 1), 16);
        bytes.CopyTo(new Span<byte>(mem, bytes.Length));
        try
        {
            return new EmojiData(mem, bytes.Length, ownsMemory: true, keepAlive: null);
        }
        catch
        {
            NativeMemory.AlignedFree(mem);
            throw;
        }
    }

    internal ReadOnlySpan<T> Section<T>(EmojiBlob.Section section) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ReadOnlySpan<T>(_base + _sectionOffset[(int)section], _sectionCount[(int)section]);
    }

    internal ReadOnlySpan<EmojiRecord> Records => Section<EmojiRecord>(EmojiBlob.Section.Emoji);

    internal ReadOnlySpan<TermRecord> Terms => Section<TermRecord>(EmojiBlob.Section.Terms);

    internal ReadOnlySpan<uint> Postings => Section<uint>(EmojiBlob.Section.Postings);

    internal ReadOnlySpan<char> Text(StringRef r) => Section<char>(EmojiBlob.Section.Chars).Slice(r.Offset, r.Length);

    internal ref readonly EmojiRecord Record(int index) => ref Records[index];

    /// <summary>The emoji itself (fully qualified).</summary>
    public ReadOnlySpan<char> Emoji(int index) => Text(Records[index].Emoji);

    /// <summary>The emoji with the given skin tone (1..5) applied, or the base emoji when it has no tone variants.</summary>
    public ReadOnlySpan<char> Emoji(int index, int tone)
    {
        ref readonly var r = ref Records[index];
        if (tone is < 1 or > 5 || r.FirstTone < 0)
            return Text(r.Emoji);
        return Text(Section<StringRef>(EmojiBlob.Section.Tones)[r.FirstTone + tone - 1]);
    }

    public bool HasSkinTones(int index) => Records[index].FirstTone >= 0;

    /// <summary>CLDR name, e.g. "face with tears of joy".</summary>
    public ReadOnlySpan<char> Name(int index) => Text(Records[index].Name);

    public int ShortcodeCount(int index) => Records[index].ShortcodeCount;

    /// <summary>Shortcode <paramref name="slot"/> of the emoji; slot 0 is the primary (Slack/GitHub) shortcode.</summary>
    public ReadOnlySpan<char> Shortcode(int index, int slot)
    {
        ref readonly var r = ref Records[index];
        return Text(Section<StringRef>(EmojiBlob.Section.Shortcodes)[r.FirstShortcode + slot]);
    }


    public int Popularity(int index) => Records[index].Popularity;

    public bool IsVariant(int index) => (Records[index].Flags & (byte)EmojiFlags.Variant) != 0;

    /// <summary>Whether the build machine's emoji font drew this emoji (the default visibility before a runtime font check).</summary>
    public bool RenderedOnReferenceFont(int index) => (Records[index].Flags & (byte)EmojiFlags.RenderedOnReferenceFont) != 0;

    /// <summary>Finds the emoji whose base form equals <paramref name="emoji"/> exactly, or -1.</summary>
    public int IndexOf(ReadOnlySpan<char> emoji)
    {
        for (var i = 0; i < Count; i++)
        {
            if (Emoji(i).SequenceEqual(emoji))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Frees the data. Optional: an instance swapped out of a live engine is simply dropped, and the finalizer
    /// frees its memory once no search can still be using it.
    /// </summary>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    ~EmojiData() => Release();

    private void Release()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsMemory)
            NativeMemory.AlignedFree(_base);
        _keepAlive?.Dispose();
    }
}
