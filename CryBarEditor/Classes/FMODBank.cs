using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Bank = FMOD.Studio.Bank;

namespace CryBarEditor;

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

#pragma warning disable CS0414
    bool _disposed;
#pragma warning restore

    FMODBank(
        FMOD.Studio.System system,
        string bankPath,
        byte[] bankData,
        byte[]? bankMasterData = null,
        byte[]? bankMasterStringsData = null)
    {
        BankPath = bankPath;

        _system = system;
        _bankData = bankData;
        _bankMasterData = bankMasterData;
        _bankMasterStringsData = bankMasterStringsData;

        (_bank, _bankMaster, _bankMasterStrings) = LoadBanksIntoSystem(_system);

        // Load events
        var r = _bank.getEventList(out var events);
        if (r != FMOD.RESULT.OK || events == null) throw new Exception("Failed to load FMOD bank event list: " + r);

        Events = new FMODEvent[events.Length];
        for (int i = 0; i < events.Length; i++)
            Events[i] = new FMODEvent(system, events[i], _bankData, _bankMasterData, bankMasterStringsData);
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

public class FMODEvent
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

            if (chunkId == "fmt ")
            {
                if (pos + 8 + 16 <= data.Length)
                {
                    channels = BitConverter.ToInt16(data, pos + 8 + 2);
                    sampleRate = BitConverter.ToInt32(data, pos + 8 + 4);
                    bitsPerSample = BitConverter.ToInt16(data, pos + 8 + 14);
                }
            }
            else if (chunkId == "data")
            {
                dataOffset = pos + 8;
                dataSize = chunkSize;
                break;
            }

            pos += 8 + chunkSize;
            if (pos % 2 != 0) pos++; // chunks are word-aligned
        }

        if (dataOffset < 0 || dataSize <= 0) return;
        if (bitsPerSample != 16) return; // only handle 16-bit PCM for trimming

        int bytesPerSample = channels * (bitsPerSample / 8);

        bool IsSilent(int byteOffset)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int off = dataOffset + byteOffset + ch * 2;
                if (off + 1 >= data.Length) continue;
                if (Math.Abs(BitConverter.ToInt16(data, off)) > threshold)
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

    static void WriteWav(string path, byte[] pcmData, int channels, int sampleRate, int bitsPerSample)
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
        bw.Write((short)1);                   // audio format (1 = PCM)
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
    {
        // Create a new Studio system for exporting
        FMOD.Studio.System exportSystem;
        FMOD.Studio.System.create(out exportSystem);

        // Set the output to WAV writer before initialization
        exportSystem.getCoreSystem(out var coreSystem);
        coreSystem.setOutput(FMOD.OUTPUTTYPE.WAVWRITER_NRT);

        // Set DSP buffer size for NRT rendering
        coreSystem.setDSPBufferSize(512, 4);

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