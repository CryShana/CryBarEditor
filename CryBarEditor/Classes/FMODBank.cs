using System;
using System.IO;
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

    bool _disposed;

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
            Events[i] = new FMODEvent(events[i]);
    }

    public async Task Play(FMOD.Studio.EventDescription e, CancellationToken token = default)
    {
        var r = e.createInstance(out var instance);
        if (r != FMOD.RESULT.OK) throw new Exception("Invalid event");

        r = instance.start();
        if (r != FMOD.RESULT.OK) throw new Exception("Invalid start");

        while (!_disposed && !token.IsCancellationRequested)
        {
            _system.update();
            instance.getPlaybackState(out var state);
            if (state == FMOD.Studio.PLAYBACK_STATE.STOPPED) break;
            await Task.Delay(10);
        }

        instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        instance.release();
    }

    public void ExportEvent(FMOD.Studio.EventDescription e, string output_wav_path)
    {
        // Create a separate system for recording
        FMOD.Studio.System.create(out var recordSystem);
        recordSystem.getCoreSystem(out var coreSystem);

        // Set to WAV writer output
        coreSystem.setOutput(FMOD.OUTPUTTYPE.WAVWRITER);
        coreSystem.init(512, FMOD.INITFLAGS.NORMAL,
            System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(output_wav_path));

        recordSystem.initialize(512, FMOD.Studio.INITFLAGS.NORMAL,
            FMOD.INITFLAGS.NORMAL, IntPtr.Zero);

        try
        {
            // reload banks in record system
            LoadBanksIntoSystem(recordSystem);

            // create event instance
            e.getID(out var id);
            recordSystem.getEventByID(id, out var eventDesc);
            eventDesc.createInstance(out var instance);

            // Get length
            eventDesc.getLength(out int lengthMs);
            if (lengthMs <= 0) lengthMs = 10000;

            instance.start();

            // Process until complete
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < lengthMs + 100)
            {
                recordSystem.update();
                instance.getPlaybackState(out var state);
                if (state == FMOD.Studio.PLAYBACK_STATE.STOPPED) break;
            }

            instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            instance.release();
        }
        finally
        {
            recordSystem.release();
        }
    }

    public void ExportAllEvents(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var fmodEvent in Events)
        {
            var outputPath = Path.Combine(outputDirectory, SanitizeFileName(fmodEvent.Path) + ".wav");
            ExportEvent(fmodEvent.eventDescription, outputPath);
        }
    }

    static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
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

    public FMODEvent(FMOD.Studio.EventDescription e)
    {
        eventDescription = e;
        e.getPath(out string? path);
        if (string.IsNullOrEmpty(path))
        {
            // try ID instead
            e.getID(out FMOD.GUID id);
            path = $"GUID_{id.Data1:X4}_{id.Data2:X4}_{id.Data3:X4}_{id.Data4:X4}";
        }

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
    }
}