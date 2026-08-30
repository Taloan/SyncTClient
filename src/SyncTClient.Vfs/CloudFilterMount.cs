using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace SyncTClient.Vfs;

/// <summary>
/// Haengt eine <see cref="IContentSource"/> als Platzhalter-Ordner in den
/// Explorer: alle Dateien sind sichtbar, keine belegt Platz, und beim ersten
/// Zugriff faehrt Windows den Hydrations-Rueckruf an.
/// </summary>
public sealed class CloudFilterMount : IDisposable
{
    /// <summary>
    /// CfAPI verlangt sektorausgerichtete Uebertragungen. 4 KiB ist die
    /// dokumentierte Groesse; nur das Dateiende darf davon abweichen.
    /// </summary>
    private const int SectorSize = 4096;

    private static readonly uint TransferDataParamSize = ComputeTransferDataParamSize();

    private readonly string _rootPath;
    private readonly IContentSource _source;
    private readonly Action<string>? _log;

    private GCHandle _self;
    private unsafe CF_CALLBACK_REGISTRATION* _callbacks;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;

    public CloudFilterMount(string rootPath, IContentSource source, Action<string>? log = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _source = source;
        _log = log;
    }

    public string RootPath => _rootPath;

    /// <summary>Verbindet die Rueckrufe. Ab hier bedient Windows Zugriffe ueber uns.</summary>
    public unsafe void Connect()
    {
        _self = GCHandle.Alloc(this);

        // Die Tabelle muss ueber die gesamte Verbindungsdauer gueltig bleiben,
        // deshalb nativ statt auf dem verschiebbaren Heap.
        _callbacks = (CF_CALLBACK_REGISTRATION*)NativeMemory.Alloc(
            2, (nuint)sizeof(CF_CALLBACK_REGISTRATION));

        _callbacks[0] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
            Callback = &OnFetchData
        };
        _callbacks[1] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
            Callback = null
        };

        fixed (char* rootPtr = _rootPath)
        fixed (CF_CONNECTION_KEY* keyPtr = &_connectionKey)
        {
            var result = PInvoke.CfConnectSyncRoot(
                rootPtr, _callbacks, (void*)GCHandle.ToIntPtr(_self),
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH
                | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO,
                keyPtr);
            Marshal.ThrowExceptionForHR(result);
        }

        _connected = true;
        _log?.Invoke($"Sync-Root verbunden: {_rootPath}");
    }

    // ------------------------------------------------------------ Platzhalter

    /// <summary>
    /// Legt fuer jeden Eintrag der Quelle einen Platzhalter an -- Verzeichnisse
    /// zuerst, damit die Kinder ein Zuhause haben.
    /// </summary>
    public void ProjectPlaceholders()
    {
        var entries = _source.Enumerate();

        // Verzeichnisse aus den Pfaden ableiten, nicht nur den ausdruecklichen
        // Eintraegen vertrauen: manche Quellen liefern nur Dateien.
        var directories = new HashSet<string> { string.Empty };
        foreach (var entry in entries)
        {
            if (entry.IsDirectory) directories.Add(entry.RelativePath);
            var slash = entry.RelativePath.LastIndexOf('/');
            while (slash > 0)
            {
                directories.Add(entry.RelativePath[..slash]);
                slash = entry.RelativePath.LastIndexOf('/', slash - 1);
            }
        }

        // Verzeichnisse werden echte Verzeichnisse, keine Platzhalter. Sie
        // haben keinen Inhalt zum Nachladen, und ein frisch angelegter
        // Verzeichnis-Platzhalter nimmt nicht zuverlaessig sofort Kinder auf --
        // CfCreatePlaceholders quittiert das mit ERROR_CLOUD_FILE_METADATA_CORRUPT
        // fuer den gesamten Stapel. Dass Windows uns nicht nach Population
        // fragt, regelt die Politik des Sync-Roots (Population = FULL).
        foreach (var directory in directories.OrderBy(d => d.Count(c => c == '/')))
        {
            if (string.IsNullOrEmpty(directory)) continue;
            Directory.CreateDirectory(
                Path.Combine(_rootPath, directory.Replace('/', Path.DirectorySeparatorChar)));
        }

        var files = entries
            .Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.RelativePath))
            .GroupBy(e => ParentOf(e.RelativePath))
            .ToDictionary(g => g.Key, g => g.ToList());

        var created = 0;
        foreach (var (directory, kids) in files)
            created += CreatePlaceholders(directory, kids);

        _log?.Invoke($"{directories.Count - 1} Verzeichnisse, {created} Platzhalter angelegt (0 Bytes belegt).");
    }

    private unsafe int CreatePlaceholders(string directory, List<VirtualEntry> entries)
    {
        var baseDirectory = string.IsNullOrEmpty(directory)
            ? _rootPath
            : Path.Combine(_rootPath, directory.Replace('/', Path.DirectorySeparatorChar));

        var infos = new CF_PLACEHOLDER_CREATE_INFO[entries.Count];
        var allocations = new List<nint>(entries.Count * 2);

        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var leaf = LeafOf(entry.RelativePath);

                var namePtr = Marshal.StringToHGlobalUni(leaf);
                // Die Identitaet kommt im Rueckruf zurueck -- wir legen den
                // vollen relativen Pfad hinein und finden die Datei damit
                // spaeter ohne Umweg wieder.
                var identityPtr = Marshal.StringToHGlobalUni(entry.RelativePath);
                allocations.Add(namePtr);
                allocations.Add(identityPtr);

                var fileTime = entry.LastWrite.ToFileTime();

                infos[i] = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = (char*)namePtr,
                    FileIdentity = (void*)identityPtr,
                    FileIdentityLength = (uint)((entry.RelativePath.Length + 1) * sizeof(char)),
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = entry.IsDirectory ? 0 : entry.Size,
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = fileTime,
                            LastWriteTime = fileTime,
                            ChangeTime = fileTime,
                            LastAccessTime = fileTime,
                            FileAttributes = entry.IsDirectory
                                ? (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY
                                : (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL
                        }
                    },
                    // MARK_IN_SYNC: der Eintrag gilt sofort als abgeglichen,
                    // sonst zeigt der Explorer dauerhaft ein Sync-Symbol.
                    //
                    // DISABLE_ON_DEMAND_POPULATION wird hier bewusst NICHT
                    // gesetzt: es erklaert ein Verzeichnis fuer vollstaendig
                    // befuellt. Da wir die Kinder erst danach anlegen, wertet
                    // Windows das als Widerspruch und liefert fuer den ganzen
                    // Stapel ERROR_CLOUD_FILE_METADATA_CORRUPT. Dass Windows
                    // uns trotzdem nicht nach Population fragt, regelt die
                    // Politik des Sync-Roots (Population = FULL).
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                };
            }

            uint processed;
            HRESULT callResult;
            fixed (CF_PLACEHOLDER_CREATE_INFO* infoPtr = infos)
            fixed (char* basePtr = baseDirectory)
            {
                callResult = PInvoke.CfCreatePlaceholders(
                    basePtr, infoPtr, (uint)infos.Length,
                    CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed);
            }

            // CfAPI haelt beim ersten fehlerhaften Eintrag an. Nicht angefasste
            // Eintraege behalten Result = S_OK, sind also nicht von Erfolgen zu
            // unterscheiden -- allein EntriesProcessed sagt, wie weit es kam.
            if (processed < entries.Count)
            {
                var stuck = entries[(int)processed];
                var code = (uint)infos[(int)processed].Result.Value;
                _log?.Invoke($"  ABBRUCH in \"{directory}\" nach {processed}/{entries.Count}: " +
                             $"\"{stuck.RelativePath}\" -> Eintrag 0x{code:X8}, " +
                             $"Aufruf 0x{(uint)callResult.Value:X8}");
            }

            // Windows leitet die Ueberlagerungssymbole -- Wolke, Kringel,
            // gruener Haken -- aus dem Anheft-Zustand ab. Ohne ihn zeigt es
            // gar keines. "Nicht angeheftet" heisst: darf verdraengt werden,
            // und genau das ist bei uns der Normalfall.
            for (var i = 0; i < (int)processed && i < entries.Count; i++)
            {
                if (entries[i].IsDirectory) continue;
                MarkUnpinned(Path.Combine(baseDirectory, LeafOf(entries[i].RelativePath)));
            }

            return (int)processed;
        }
        finally
        {
            foreach (var allocation in allocations)
                Marshal.FreeHGlobal(allocation);
        }
    }


    /// <summary>
    /// Setzt den Anheft-Zustand auf "nicht angeheftet". Angeheftete Dateien
    /// waeren solche, die Windows immer lokal halten soll -- unsere sollen
    /// bei Bedarf kommen und wieder gehen duerfen.
    /// </summary>
    private unsafe void MarkUnpinned(string fullPath)
    {
        try
        {
            using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var result = PInvoke.CfSetPinState(
                handle, CF_PIN_STATE.CF_PIN_STATE_UNPINNED, CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE, null);

            if (result.Failed)
                _log?.Invoke($"  Anheft-Zustand fuer \"{Path.GetFileName(fullPath)}\": 0x{(uint)result.Value:X8}");
        }
        catch (IOException) { /* in Benutzung -- beim naechsten Start erneut */ }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------ Hydration

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var mount = (CloudFilterMount)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;

            // Die Zeiger gelten nur waehrend des Rueckrufs. Alles Noetige
            // kopieren, bevor wir asynchron werden.
            // Wer fragt? Bei voller Hydration trotz PARTIAL-Politik ist die
            // erste Frage, ob ueberhaupt der Nutzer der Ausloeser ist -- ein
            // Virenscanner liest die Datei beim Oeffnen komplett.
            var caller = info->ProcessInfo is null
                ? "(unbekannt)"
                : $"{Marshal.PtrToStringUni((nint)info->ProcessInfo->ImagePath.Value) ?? "?"} " +
                  $"(PID {info->ProcessInfo->ProcessId})";

            var request = new HydrationRequest(
                info->ConnectionKey,
                info->TransferKey,
                Marshal.PtrToStringUni((nint)info->FileIdentity) ?? string.Empty,
                parameters->Anonymous.FetchData.RequiredFileOffset,
                parameters->Anonymous.FetchData.RequiredLength,
                info->FileSize,
                caller,
                parameters->Anonymous.FetchData.OptionalFileOffset,
                parameters->Anonymous.FetchData.OptionalLength);

            // Der Rueckruf darf nicht blockieren -- Windows wartet sonst auf
            // uns, waehrend wir auf das Netz warten.
            _ = Task.Run(() => mount.ServeAsync(request));
        }
        catch
        {
            // Aus einem UnmanagedCallersOnly-Rueckruf darf nichts herausfliegen.
        }
    }

    private readonly record struct HydrationRequest(
        CF_CONNECTION_KEY ConnectionKey,
        long TransferKey,
        string RelativePath,
        long RequiredOffset,
        long RequiredLength,
        long FileSize,
        string Caller,
        long OptionalOffset,
        long OptionalLength);

    private async Task ServeAsync(HydrationRequest request)
    {
        // Windows verlangt einen sektorausgerichteten Anfang; die Laenge darf
        // nur am Dateiende davon abweichen.
        var start = request.RequiredOffset & ~((long)SectorSize - 1);
        var end = request.RequiredOffset + request.RequiredLength;
        var alignedEnd = Math.Min(request.FileSize, (end + SectorSize - 1) & ~((long)SectorSize - 1));
        var length = alignedEnd - start;

        try
        {
            _log?.Invoke($"Hydration: {request.RelativePath}");
            _log?.Invoke($"  verlangt [{request.RequiredOffset}..{request.RequiredOffset + request.RequiredLength}), " +
                         $"optional [{request.OptionalOffset}..{request.OptionalOffset + request.OptionalLength})");
            _log?.Invoke($"  Ausloeser: {request.Caller}");

            var data = await _source.ReadAsync(request.RelativePath, start, length, CancellationToken.None)
                .ConfigureAwait(false);

            TransferData(request, data, start);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Hydration fehlgeschlagen fuer {request.RelativePath}: {ex.Message}");
            // STATUS_UNSUCCESSFUL -- der Zugriff scheitert, statt haengenzubleiben.
            TransferFailure(request, start);
        }
    }

    /// <summary>Kopiert die Nutzdaten in sektorausgerichteten Speicher und reicht sie durch.</summary>
    private unsafe void TransferData(HydrationRequest request, byte[] data, long offset)
    {
        var buffer = NativeMemory.AlignedAlloc((nuint)data.Length, SectorSize);
        try
        {
            data.AsSpan().CopyTo(new Span<byte>(buffer, data.Length));
            Transfer(request, buffer, offset, data.Length, ntStatus: 0);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private unsafe void TransferFailure(HydrationRequest request, long offset)
        => Transfer(request, null, offset, 0, unchecked((int)0xC0000001));

    private unsafe void Transfer(HydrationRequest request, void* buffer, long offset, long length, int ntStatus)
    {
        var operation = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = request.ConnectionKey,
            TransferKey = request.TransferKey
        };

        var parameters = new CF_OPERATION_PARAMETERS { ParamSize = TransferDataParamSize };
        parameters.Anonymous.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
        parameters.Anonymous.TransferData.CompletionStatus = new NTSTATUS(ntStatus);
        parameters.Anonymous.TransferData.Buffer = buffer;
        parameters.Anonymous.TransferData.Offset = offset;
        parameters.Anonymous.TransferData.Length = length;

        var result = PInvoke.CfExecute(&operation, &parameters);
        if (result.Failed)
        {
            // 0x8007018E: die Anfrage wurde storniert. Das ist der Normalfall,
            // wenn ein zweiter, groesserer Abruf denselben Bereich schon
            // gefuellt hat -- kein Fehler, nur vergebene Muehe.
            const uint requestCanceled = 0x8007018E;
            if ((uint)result.Value == requestCanceled)
                _log?.Invoke("  (Anfrage storniert -- Bereich war bereits gefuellt)");
            else
                _log?.Invoke($"  CfExecute schlug fehl: 0x{(uint)result.Value:X8}");
        }
    }

    /// <summary>
    /// Entspricht dem Makro CF_SIZE_OF_OP_PARAM(TransferData): Feldversatz
    /// innerhalb der Union plus Groesse des Feldes.
    /// </summary>
    private static unsafe uint ComputeTransferDataParamSize()
    {
        CF_OPERATION_PARAMETERS probe = default;
        var offset = (byte*)&probe.Anonymous.TransferData - (byte*)&probe;
        return (uint)(offset + sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct));
    }

    // ------------------------------------------------------------ Hilfsmittel

    private static string ParentOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static string LeafOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    public unsafe void Dispose()
    {
        if (_connected)
        {
            PInvoke.CfDisconnectSyncRoot(_connectionKey);
            _connected = false;
        }

        if (_callbacks is not null)
        {
            NativeMemory.Free(_callbacks);
            _callbacks = null;
        }

        if (_self.IsAllocated) _self.Free();
    }
}
