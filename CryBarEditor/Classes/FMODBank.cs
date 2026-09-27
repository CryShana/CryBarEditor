using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Bank = FMOD.Studio.Bank;

namespace CryBarEditor.Classes;

public class FMODBank : IDisposable
{
    readonly byte[] _bankData;
    readonly byte[]? _bankMasterData;
    readonly byte[]? _bankMasterStringsData;

    readonly FMOD.Studio.System _system;
    readonly Bank _bank;
    readonly Bank? _bankMaster;
    readonly Bank? _bankMasterStrings;

    public string BankPath { get; }

    public FMODEvent[] Events { get; init; }
    public FMODSubsound[] Subsounds { get; init; }

#pragma warning disable CS0414
    bool _disposed;
#pragma warning restore

    FMODBank(
        FMOD.Studio.System system,
        string bankPath,
        byte[] bankData,
        byte[]? bankMasterData = null,
        byte[]? bankMasterStringsData = null,
        CancellationToken token = default)
    {
        BankPath = bankPath;

        _system = system;
        _bankData = bankData;
        _bankMasterData = bankMasterData;
        _bankMasterStringsData = bankMasterStringsData;

        (_bank, _bankMaster, _bankMasterStrings) = LoadBanksIntoSystem(_system);
        token.ThrowIfCancellationRequested();

        // Load events (asset/streaming banks like music.bank have none; that's expected)
        var r = _bank.getEventList(out var events);
        if (r != FMOD.RESULT.OK) throw new Exception("Failed to load FMOD bank event list: " + r);

        events ??= [];
        Events = new FMODEvent[events.Length];
        for (int i = 0; i < events.Length; i++)
            Events[i] = new FMODEvent(system, events[i], _bankData, _bankMasterData, bankMasterStringsData);

        // Load audio embedded directly as FSB5 subsounds (not exposed as events)
        Subsounds = BuildSubsounds(system, token);
    }

    /// <summary>
    /// Enumerates the bank's embedded FSB5 sound banks and exposes each subsound.
    /// Asset/streaming banks (e.g. music.bank) carry their audio here with no events.
    /// </summary>
    FMODSubsound[] BuildSubsounds(FMOD.Studio.System system, CancellationToken token)
    {
        var ranges = FindFsb5Ranges(_bankData);
        if (ranges.Count == 0) return [];

        system.getCoreSystem(out var core);

        var list = new List<FMODSubsound>();
        foreach (var (start, length) in ranges)
        {
            // Point FMOD at the FSB5 in place via fileoffset - no per-range copy of the bank.
            var sound = OpenFsb(core, start, length, FMOD.MODE.CREATECOMPRESSEDSAMPLE, out var r);
            if (r != FMOD.RESULT.OK) continue;

            sound.getNumSubSounds(out var count);
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();

                sound.getSubSound(i, out var sub);
                sub.getName(out var name, 256);
                sub.getLength(out var lengthMs, FMOD.TIMEUNIT.MS);
                list.Add(new FMODSubsound(_bankData, (uint)start, length, i, name, (int)lengthMs));
            }

            sound.release();
        }

        return list.ToArray();
    }

    /// <summary>
    /// Opens the FSB5 container at <paramref name="fileOffset"/> directly from the shared
    /// bank bytes (no copy). Caller supplies the extra mode flags (CREATECOMPRESSEDSAMPLE
    /// for fast header enumeration, CREATESTREAM for playback/export).
    /// </summary>
    FMOD.Sound OpenFsb(FMOD.System core, int fileOffset, uint length, FMOD.MODE mode, out FMOD.RESULT r)
    {
        // length must be the container size, not the whole bank: with OPENMEMORY + fileoffset
        // FMOD reads `length` bytes starting AT fileoffset, so passing the full bank length
        // overruns the buffer by fileoffset bytes and crashes the runtime on large banks.
        var ex = new FMOD.CREATESOUNDEXINFO();
        ex.cbsize = Marshal.SizeOf(ex);
        ex.length = length;
        ex.fileoffset = (uint)fileOffset;

        r = core.createSound(_bankData, FMOD.MODE.OPENMEMORY | mode, ref ex, out var sound);
        return sound;
    }

    /// <summary>
    /// Returns the byte ranges of each genuine "FSB5" container inside the bank.
    /// The 4-byte magic also appears by coincidence inside compressed audio, so each
    /// candidate is validated against its self-declared header - feeding a bogus offset
    /// to createSound makes FMOD read far out of bounds and crash the runtime.
    /// </summary>
    static List<(int start, uint length)> FindFsb5Ranges(byte[] data)
    {
        ReadOnlySpan<byte> magic = "FSB5"u8;
        var span = (ReadOnlySpan<byte>)data;

        // Vectorized scan - a manual byte loop over a ~474MB bank costs hundreds of ms.
        var ranges = new List<(int, uint)>();
        int from = 0;
        while (from <= span.Length - magic.Length)
        {
            int rel = span[from..].IndexOf(magic);
            if (rel < 0) break;

            int start = from + rel;
            if (TryReadFsb5Size(data, start, out long total))
            {
                ranges.Add((start, (uint)total));
                from = start + (int)total; // skip the container body - it can't hold another header
            }
            else
            {
                from = start + magic.Length;
            }
        }

        return ranges;
    }

    /// <summary>
    /// Validates the FSB5 header at <paramref name="offset"/> and returns the container's
    /// total byte size. Rejects coincidental magic matches whose header fields are
    /// nonsensical or describe a container that would not fit inside the buffer.
    /// </summary>
    static bool TryReadFsb5Size(byte[] data, int offset, out long total)
    {
        total = 0;

        const int HeaderSize = 0x3C; // FSB5 base header (v0/v1)
        if (offset < 0 || offset + HeaderSize > data.Length)
            return false;

        uint version    = BitConverter.ToUInt32(data, offset + 0x04);
        uint numSamples = BitConverter.ToUInt32(data, offset + 0x08);
        uint shdrSize   = BitConverter.ToUInt32(data, offset + 0x0C);
        uint nameSize   = BitConverter.ToUInt32(data, offset + 0x10);
        uint dataSize   = BitConverter.ToUInt32(data, offset + 0x14);

        if (version != 0 && version != 1) return false;
        if (numSamples == 0 || numSamples > 1_000_000) return false;
        if (shdrSize == 0 || dataSize == 0) return false;

        total = HeaderSize + (long)shdrSize + nameSize + dataSize;
        return offset + total <= data.Length;
    }

    (Bank bank, Bank? bank_master, Bank? bank_strings) LoadBanksIntoSystem(FMOD.Studio.System system)
    {
        Bank? bank_strings_out = null;
        Bank? bank_master_out = null;

        FMOD.RESULT r;
        // MASTER Strings bank should be loaded first for paths
        if (_bankMasterStringsData != null)
        {
            r = system.loadBankMemory(_bankMasterStringsData, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank_strings);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to load master strings  FMOD bank: " + r);

            bank_strings_out = bank_strings;
        }

        // MASTER bank should be loaded second for samples and other stuff FMOD needs
        if (_bankMasterData != null)
        {
            r = system.loadBankMemory(_bankMasterData, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank_master);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to load master FMOD bank: " + r);

            bank_master_out = bank_master;

            r = bank_master.loadSampleData();
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to load master FMOD bank samples: " + r);
        }

        // Now we load the target bank
        r = system.loadBankMemory(_bankData, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank);
        if (r != FMOD.RESULT.OK) throw new Exception("Failed to load FMOD bank: " + r);

        r = bank.loadSampleData();
        if (r != FMOD.RESULT.OK) throw new Exception("Failed to load FMOD bank samples: " + r);

        return (bank, bank_master_out, bank_strings_out);
    }

    public void Dispose()
    {
        _disposed = true;

        _bank.unload();
        _bankMaster?.unload();
        _bankMasterStrings?.unload();
        _system.release();
    }

    public static FMODBank? LoadBank(string bank_path)
    {
        if (Path.GetExtension(bank_path).ToLower() != ".bank")
            throw new Exception("Not a BANK file");

        var parent_dir = Path.GetDirectoryName(bank_path);
        if (parent_dir == null)
            throw new Exception("Invalid parent directory");

        var name = Path.GetFileNameWithoutExtension(bank_path);

        FMOD.Studio.System studio = default;
        try
        {
            FMOD.Studio.System.create(out studio);
            var r = studio.initialize(512, FMOD.Studio.INITFLAGS.NORMAL, FMOD.INITFLAGS.NORMAL, nint.Zero);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to initialize FMOD system: " + r);

            byte[] bankData = File.ReadAllBytes(bank_path);
            byte[]? bankMasterData = null;
            byte[]? bankMasterStringsData = null;

            if (name != "Master.strings")
            {
                var full_path = Path.Combine(parent_dir, "Master.strings.bank");
                if (File.Exists(full_path))
                    bankMasterStringsData = File.ReadAllBytes(full_path);
            }

            // Secondly load the master bank, this contains the basics to play/export actual sounds
            if (name != "Master")
            {
                var full_path = Path.Combine(parent_dir, "Master.bank");
                if (File.Exists(full_path))
                    bankMasterData = File.ReadAllBytes(full_path);
            }

            return new FMODBank(studio, bank_path, bankData,
                bankMasterData, bankMasterStringsData);
        }
        catch
        {
            studio.release();
            throw;
        }
    }
}

/// <summary>
/// A playable/exportable entry in an FMOD bank - either an <see cref="FMODEvent"/>
/// or an <see cref="FMODSubsound"/> (raw audio from an asset bank's FSB5).
/// </summary>
public interface IBankItem : INotifyPropertyChanged
{
    /// <summary>Label shown in the bank entry list.</summary>
    string DisplayName { get; }

    /// <summary>True for events, false for raw FSB5 subsounds.</summary>
    bool IsEvent { get; }

    int LengthMs { get; }

    /// <summary>True while this entry is the one currently being played back.</summary>
    bool IsPlaying { get; set; }

    Task Play(CancellationToken token = default);
    void Export(string output_path_wav, CancellationToken token = default);
}

public abstract class BankItemBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }
}

public class FMODEvent : BankItemBase, IBankItem
{
    public string Id { get; set; }
    public string Path { get; set; }
    public int LengthMs { get; set; }
    public bool Is3D { get; set; }
    public bool IsOneshot { get; set; }
    public bool IsSnapshot { get; set; }
    public float MinDistance { get; set; }
    public float MaxDistance { get; set; }
    public bool IsDopplerEnabled { get; set; }
    public string[] Parameters { get; set; }

    public string DisplayName => Path;
    public bool IsEvent => true;

    public readonly FMOD.Studio.EventDescription eventDescription;
    readonly FMOD.Studio.System _system;
    readonly byte[] _bankData;
    readonly byte[]? _bankMasterData;
    readonly byte[]? _bankMasterStringsData;

    public FMODEvent(FMOD.Studio.System system, FMOD.Studio.EventDescription e, byte[] bank, byte[]? bankMaster, byte[]? bankMasterStrings)
    {
        _system = system;
        _bankData = bank;
        _bankMasterData = bankMaster;
        _bankMasterStringsData = bankMasterStrings;

        eventDescription = e;
        e.getPath(out string? path);
        if (string.IsNullOrEmpty(path))
        {
            path = $"No path found";
        }

        e.getID(out FMOD.GUID id);
        Id = $"{{{id.Data1:x8}-{id.Data2:x8}-{id.Data3:x8}-{id.Data4:x8}}}"; // FMOD uses a slightly different format of displaying IDs, but unsure what

        // Get more useful info
        e.getLength(out int length);
        e.is3D(out bool is3D);
        e.isOneshot(out bool isOneshot);
        e.isSnapshot(out bool isSnapshot);
        e.getUserPropertyCount(out int userPropCount);
        e.getMinMaxDistance(out float minDist, out float maxDist);
        e.isDopplerEnabled(out bool doppler);
        e.getParameterDescriptionCount(out int paramCount);

        Path = path;
        LengthMs = length;
        Is3D = is3D;
        IsOneshot = isOneshot;
        IsSnapshot = isSnapshot;
        MinDistance = minDist;
        MaxDistance = maxDist;
        IsDopplerEnabled = doppler;

        Parameters = new string[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            e.getParameterDescriptionByIndex(i, out var prm);

            string name = prm.name;
            Parameters[i] = $"{name} ({prm.type})";
        }

        // use system to discover sound files

    }

    public async Task Play(CancellationToken token = default)
    {
        var e = eventDescription;

        var r = e.createInstance(out var instance);
        if (r != FMOD.RESULT.OK) throw new Exception("Invalid event");

        r = instance.start();
        if (r != FMOD.RESULT.OK) throw new Exception("Invalid start");

        while (!token.IsCancellationRequested)
        {
            _system.update();
            instance.getPlaybackState(out var state);
            if (state == FMOD.Studio.PLAYBACK_STATE.STOPPED) break;
            await Task.Delay(10);
        }

        instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        instance.release();
    }

    /// <summary>
    /// Trims leading and trailing silence from a WAV file and rewrites it in place.
    /// Silence is defined as samples below a small threshold.
    /// </summary>
    public static void TrimSilence(string wavPath, short threshold = 16)
    {
        var data = File.ReadAllBytes(wavPath);
        if (data.Length < 44) return; // too small for a valid WAV

        // Parse WAV header to find data chunk
        int dataOffset = -1;
        int dataSize = -1;
        int channels = 1;
        int sampleRate = 44100;
        int bitsPerSample = 16;

        int pos = 12; // skip RIFF header
        while (pos + 8 <= data.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            int chunkSize = BitConverter.ToInt32(data, pos + 4);
            int remaining = data.Length - pos - 8;

            if (chunkId == "data")
            {
                dataOffset = pos + 8;
                dataSize = chunkSize < 0 ? remaining : Math.Min(chunkSize, remaining);
                break;
            }

            if (chunkSize < 0 || chunkSize > remaining) break;

            if (chunkId == "fmt ")
            {
                if (pos + 8 + 16 <= data.Length)
                {
                    channels = BitConverter.ToInt16(data, pos + 8 + 2);
                    sampleRate = BitConverter.ToInt32(data, pos + 8 + 4);
                    bitsPerSample = BitConverter.ToInt16(data, pos + 8 + 14);
                }
            }

            pos += 8 + chunkSize;
            if (pos % 2 != 0) pos++; // chunks are word-aligned
        }

        if (dataOffset < 0 || dataSize <= 0) return;
        if (bitsPerSample != 16) return; // only handle 16-bit PCM for trimming
        if (channels <= 0) return;

        int bytesPerSample = channels * (bitsPerSample / 8);

        bool IsSilent(int byteOffset)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int off = dataOffset + byteOffset + ch * 2;
                if (off + 1 >= data.Length) continue;
                if (Math.Abs((int)BitConverter.ToInt16(data, off)) > threshold)
                    return false;
            }
            return true;
        }

        // Find first non-silent sample (leading trim)
        int firstNonSilent = 0;
        for (int i = 0; i < dataSize; i += bytesPerSample)
        {
            if (!IsSilent(i)) { firstNonSilent = i; break; }
        }

        // Find last non-silent sample (trailing trim)
        int lastNonSilent = dataSize - bytesPerSample;
        for (int i = dataSize - bytesPerSample; i >= firstNonSilent; i -= bytesPerSample)
        {
            if (!IsSilent(i)) { lastNonSilent = i; break; }
        }

        int trimmedSize = lastNonSilent - firstNonSilent + bytesPerSample;
        if (trimmedSize <= 0 || (firstNonSilent == 0 && trimmedSize == dataSize))
            return; // nothing to trim

        var trimmedPcm = new byte[trimmedSize];
        Array.Copy(data, dataOffset + firstNonSilent, trimmedPcm, 0, trimmedSize);

        WriteWav(wavPath, trimmedPcm, channels, sampleRate, bitsPerSample);
    }

    internal static void WriteWav(string path, byte[] pcmData, int channels, int sampleRate, int bitsPerSample, bool floatFormat = false)
    {
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + pcmData.Length); // chunk size
        bw.Write("WAVE"u8);

        // fmt subchunk
        bw.Write("fmt "u8);
        bw.Write(16);                         // subchunk1 size (PCM)
        bw.Write((short)(floatFormat ? 3 : 1)); // audio format (1 = PCM, 3 = IEEE float)
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)bitsPerSample);

        // data subchunk
        bw.Write("data"u8);
        bw.Write(pcmData.Length);
        bw.Write(pcmData);
    }

    public void Export(string output_path_wav, CancellationToken token = default)
        => Export(output_path_wav, token, null);

    /// <param name="randomSeed">
    /// Seeds FMOD's RNG so a multi-variant event picks differently each render. Without an explicit
    /// per-render seed, back-to-back exports keep picking the same variant; distinct seeds surface
    /// every variant.
    /// </param>
    public void Export(string output_path_wav, CancellationToken token, uint? randomSeed)
    {
        // Create a new Studio system for exporting
        FMOD.Studio.System exportSystem;
        FMOD.Studio.System.create(out exportSystem);

        // Set the output to WAV writer before initialization
        exportSystem.getCoreSystem(out var coreSystem);
        coreSystem.setOutput(FMOD.OUTPUTTYPE.WAVWRITER_NRT);

        // Set DSP buffer size for NRT rendering
        coreSystem.setDSPBufferSize(512, 4);

        if (randomSeed.HasValue)
        {
            var adv = new FMOD.ADVANCEDSETTINGS();
            coreSystem.getAdvancedSettings(ref adv);
            adv.randomSeed = randomSeed.Value;
            coreSystem.setAdvancedSettings(ref adv);
        }

        // Convert path to IntPtr
        nint pathPtr = Marshal.StringToHGlobalAnsi(output_path_wav);

        Bank bank = default;
        Bank? bankMaster = null;
        Bank? bankMasterStrings = null;
        try
        {
            // Initialize with WAV file path
            var r = exportSystem.initialize(512, FMOD.Studio.INITFLAGS.NORMAL, FMOD.INITFLAGS.NORMAL, pathPtr);
            if (r != FMOD.RESULT.OK) throw new Exception($"Failed to initialize export system: {r}");

            // Reload the same banks into this new system
            if (_bankMasterStringsData != null)
            {
                exportSystem.loadBankMemory(_bankMasterStringsData, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank_master_strings);
                bankMasterStrings = bank_master_strings;
            }

            if (_bankMasterData != null)
            {
                exportSystem.loadBankMemory(_bankMasterData, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank_master);
                bankMaster = bank_master;
            }

            exportSystem.loadBankMemory(_bankData, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out bank);
            
            // get same event again from the new system
            eventDescription.getID(out var eventId);
            exportSystem.getEventByID(eventId, out var exportEventDescription);

            // start the instance
            r = exportEventDescription.createInstance(out var instance);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to create event instance");

            r = instance.start();
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to start event");

            // process audio in non-realtime
            int updateCount = 0;
            const int maxUpdates = 10000; // Safety limit

            while (!token.IsCancellationRequested && updateCount < maxUpdates)
            {
                exportSystem.update();

                instance.getPlaybackState(out var state);
                if (state == FMOD.Studio.PLAYBACK_STATE.STOPPED)
                    break;

                updateCount++;
            }

            // Clean up
            instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            instance.release();
        }
        finally
        {
            bank.unload();
            if (bankMaster.HasValue)
                bankMaster.Value.unload();

            if (bankMasterStrings.HasValue)
                bankMasterStrings.Value.unload();

            exportSystem.release();

            Marshal.FreeHGlobal(pathPtr);
        }
    }

}

/// <summary>
/// A single subsound inside an FMOD bank's embedded FSB5 sound bank. Used for asset
/// banks (e.g. music.bank) whose audio is not exposed as events. Self-contained: each
/// Play/Export spins up its own FMOD system from the sliced FSB5 bytes.
/// </summary>
public sealed class FMODSubsound : BankItemBase, IBankItem
{
    readonly byte[] _bankData;
    readonly uint _fileOffset;
    readonly uint _length;
    readonly int _index;

    public string Name { get; }
    public int LengthMs { get; }

    public string DisplayName => Name;
    public bool IsEvent => false;

    // --- Resolved file path (populated by the editor from soundmanifest/soundset data) ---
    // FMOD subsounds carry no path; the editor matches Name against the sound path index and
    // fills these in after the bank loads. Null/empty until (and unless) a match is found.
    // These are read in code only (not data-bound), so plain auto-properties suffice.

    /// <summary>First resolved relative path (e.g. "music\aotg_theme\x.wav"), or null.</summary>
    public string? ResolvedPath { get; set; }

    /// <summary>All resolved candidate paths (more than one = ambiguous). Empty if unresolved.</summary>
    public IReadOnlyList<string> ResolvedPaths { get; set; } = [];

    /// <summary>
    /// First resolved path prefixed with the sound root (Sound.bar root), e.g.
    /// "game\sound\music\aotg_theme\x.wav". Computed and stored by the editor.
    /// </summary>
    public string? FullRelativePath { get; set; }

    public FMODSubsound(byte[] bankData, uint fileOffset, uint length, int index, string? name, int lengthMs)
    {
        _bankData = bankData;
        _fileOffset = fileOffset;
        _length = length;
        _index = index;
        Name = string.IsNullOrEmpty(name) ? $"subsound_{index}" : name;
        LengthMs = lengthMs;
    }

    FMOD.Sound CreateSubSound(FMOD.System core, FMOD.MODE extraMode, out FMOD.Sound parent)
    {
        // Share the bank bytes and seek to the FSB5 via fileoffset instead of copying it out.
        // length is the container size: full-buffer length + fileoffset overruns and crashes.
        var ex = new FMOD.CREATESOUNDEXINFO();
        ex.cbsize = Marshal.SizeOf(ex);
        ex.length = _length;
        ex.fileoffset = _fileOffset;
        ex.initialsubsound = _index; // a stream decodes only its active subsound; readData on a non-initial one reads the wrong region

        var r = core.createSound(_bankData, FMOD.MODE.OPENMEMORY | FMOD.MODE.CREATESTREAM | extraMode, ref ex, out parent);
        if (r != FMOD.RESULT.OK) throw new Exception("Failed to create sound from FSB5: " + r);

        r = parent.getSubSound(_index, out var sub);
        if (r != FMOD.RESULT.OK) throw new Exception("Failed to get FSB5 subsound: " + r);

        return sub;
    }

    public async Task Play(CancellationToken token = default)
    {
        FMOD.Studio.System.create(out var studio);
        try
        {
            var r = studio.initialize(32, FMOD.Studio.INITFLAGS.NORMAL, FMOD.INITFLAGS.NORMAL, nint.Zero);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to initialize FMOD system: " + r);

            studio.getCoreSystem(out var core);
            var sound = CreateSubSound(core, FMOD.MODE.DEFAULT, out var parent);

            r = core.playSound(sound, default, false, out var channel);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to play subsound: " + r);

            while (!token.IsCancellationRequested)
            {
                core.update();
                channel.isPlaying(out var playing);
                if (!playing) break;
                await Task.Delay(10);
            }

            channel.stop();
            parent.release();
        }
        finally
        {
            studio.release();
        }
    }

    public void Export(string output_path_wav, CancellationToken token = default)
    {
        // Decode the subsound's PCM directly. Real-time NRT rendering of the FSB5 stream
        // re-reads a small window and loops it, turning a few seconds into minutes of garbage.
        FMOD.Studio.System.create(out var studio);
        try
        {
            var r = studio.initialize(32, FMOD.Studio.INITFLAGS.NORMAL, FMOD.INITFLAGS.NORMAL, nint.Zero);
            if (r != FMOD.RESULT.OK) throw new Exception("Failed to initialize FMOD system: " + r);

            studio.getCoreSystem(out var core);
            // ACCURATETIME so FMOD accounts for the codec's encoder delay/padding - without it
            // the decode drops samples off the head and tail of short clips.
            var sound = CreateSubSound(core, FMOD.MODE.ACCURATETIME, out var parent);
            try
            {
                sound.setMode(FMOD.MODE.LOOP_OFF); // so readData reports EOF at the true end
                sound.seekData(0);                 // start at the subsound's first sample

                sound.getFormat(out _, out var format, out int channels, out int bits);
                sound.getDefaults(out float frequency, out _);
                sound.getLength(out uint pcmBytes, FMOD.TIMEUNIT.PCMBYTES); // exact, thanks to ACCURATETIME

                // Read up to the exact PCM length. The codec's final decode block can overrun
                // (repeating a few ms past the true end), so it is trimmed below. Fall back to a
                // generous bound only if the exact length is unavailable.
                long limit = pcmBytes > 0 ? pcmBytes : ((long)LengthMs / 1000 + 10) * 48000 * 2 * 4;

                using var ms = new MemoryStream(pcmBytes > 0 ? (int)pcmBytes : 0);
                var chunk = new byte[64 * 1024];
                while (ms.Length < limit)
                {
                    token.ThrowIfCancellationRequested();

                    r = sound.readData(chunk, out uint read);
                    if (read > 0) ms.Write(chunk, 0, (int)read);
                    if (r != FMOD.RESULT.OK) break; // ERR_FILE_EOF at the end
                }

                var pcm = ms.ToArray();
                if (pcmBytes > 0 && pcm.Length > pcmBytes)
                    Array.Resize(ref pcm, (int)pcmBytes); // drop the codec's tail padding/repeat

                FMODEvent.WriteWav(output_path_wav, pcm, channels, (int)frequency, bits,
                    format == FMOD.SOUND_FORMAT.PCMFLOAT);
            }
            finally
            {
                parent.release();
            }
        }
        finally
        {
            studio.release();
        }
    }
}