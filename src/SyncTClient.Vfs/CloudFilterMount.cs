using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace SyncTClient.Vfs;

/// <summary>
/// Haengt eine <see cref="IContentSource"/> als Platzhalter-Ordner in den
/// Explorer. Alle Dateien sind sichtbar, keine belegt Platz, und beim ersten
/// Zugriff ruft Windows den Hydrations-Rueckruf auf.
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
    private readonly string _volumeRelativeRoot;
    private readonly IContentSource _source;
    private readonly Action<string>? _log;

    private GCHandle _self;
    private unsafe CF_CALLBACK_REGISTRATION* _callbacks;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;

    public CloudFilterMount(string rootPath, IContentSource source, Action<string>? log = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _volumeRelativeRoot = StripDriveLetter(_rootPath).TrimEnd('\\');
        _source = source;
        _log = log;
    }

    public string RootPath => _rootPath;

    /// <summary>
    /// Ein Handle auf eine Datei der Freigabe wurde geschlossen. Der Parameter
    /// ist der relative Pfad.
    /// </summary>
    /// <remarks>
    /// Das Ereignis sagt nichts darueber aus, ob die Datei geaendert wurde.
    /// Auch reines Lesen loest es aus. Ob eine Aenderung vorliegt, entscheidet
    /// die obere Schicht anhand des In-Sync-Zustands.
    /// </remarks>
    public event Action<string>? FileClosed;

    /// <summary>Eine Datei der Freigabe wurde geloescht. Relativer Pfad.</summary>
    public event Action<string>? FileDeleted;

    /// <summary>
    /// Eine Datei wurde umbenannt oder verschoben. Erst der alte, dann der
    /// neue relative Pfad.
    /// </summary>
    public event Action<string, string>? FileRenamed;

    /// <summary>
    /// Verbindet die Rueckrufe. Ab hier leitet Windows Zugriffe hierher
    /// weiter.
    /// </summary>
    public unsafe void Connect()
    {
        _self = GCHandle.Alloc(this);

        // Die Tabelle muss ueber die gesamte Verbindungsdauer gueltig bleiben,
        // deshalb nativ statt auf dem verschiebbaren Heap.
        _callbacks = (CF_CALLBACK_REGISTRATION*)NativeMemory.Alloc(
            5, (nuint)sizeof(CF_CALLBACK_REGISTRATION));

        _callbacks[0] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
            Callback = &OnFetchData
        };
        // Der Abschluss-Rueckruf kommt einmal je geschlossenem Handle, nicht
        // bei jedem Schreibvorgang. Damit ist er der genaueste Ausloeser fuer
        // eine abgeschlossene lokale Aenderung.
        _callbacks[1] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,
            Callback = &OnFileCloseCompletion
        };
        _callbacks[2] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION,
            Callback = &OnDeleteCompletion
        };
        _callbacks[3] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION,
            Callback = &OnRenameCompletion
        };
        _callbacks[4] = new CF_CALLBACK_REGISTRATION
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

        // Ohne diese Meldung gilt der Anbieter als nicht betriebsbereit.
        // Windows fragt den Zustand ab, bevor es die Erweiterungen eines
        // Sync-Roots benutzt. Ein Anbieter, der ihn nie meldet, wird
        // uebergangen.
        ReportIdle();

        _log?.Invoke($"Sync-Root verbunden: {_rootPath}");
    }

    /// <summary>
    /// Meldet der Cloud-Filter-Schicht, was wir gerade tun.
    /// </summary>
    /// <remarks>
    /// Der Zustand erscheint in der Statusanzeige des Explorers. Ausserdem
    /// steuert er, ob Windows diesen Anbieter fuer Vorschauen und aehnliche
    /// Dienste beruecksichtigt.
    /// </remarks>
    public void ReportIdle() => ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);

    /// <summary>Meldet, dass gerade Inhalte nachgeladen werden.</summary>
    public void ReportBusy() => ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_POPULATE_CONTENT);

    private void ReportStatus(CF_SYNC_PROVIDER_STATUS status)
    {
        if (!_connected) return;

        var result = PInvoke.CfUpdateSyncProviderStatus(_connectionKey, status);
        if (result.Failed)
            _log?.Invoke($"Anbieterstatus {status}: 0x{(uint)result.Value:X8}");
    }

    // ------------------------------------------------------------ Platzhalter

    /// <summary>
    /// Legt fuer jeden Eintrag der Quelle einen Platzhalter an. Verzeichnisse
    /// werden zuerst angelegt, damit die enthaltenen Eintraege einen
    /// uebergeordneten Ordner haben.
    /// </summary>
    /// <param name="progress">
    /// Meldet angelegte und insgesamt zu erwartende Platzhalter. Bei grossen
    /// Freigaben dauert dieser Lauf lange. Ohne Rueckmeldung wirkt das Fenster
    /// waehrenddessen wie abgestuerzt.
    /// </param>
    public void ProjectPlaceholders(Action<int, int>? progress = null)
    {
        var entries = _source.Enumerate();

        // Verzeichnisse aus den Pfaden ableiten, nicht nur den ausdruecklichen
        // Eintraegen vertrauen. Manche Quellen liefern nur Dateien.
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
        // Verzeichnis-Platzhalter nimmt untergeordnete Eintraege nicht
        // zuverlaessig sofort auf. CfCreatePlaceholders meldet dann
        // ERROR_CLOUD_FILE_METADATA_CORRUPT fuer den gesamten Stapel. Dass
        // Windows nicht nach Population fragt, regelt die Politik des
        // Sync-Roots (Population = FULL).
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

        var erwartet = files.Sum(g => g.Value.Count);
        progress?.Invoke(0, erwartet);

        var created = 0;
        foreach (var (directory, kids) in files)
        {
            created += CreatePlaceholders(directory, kids);
            progress?.Invoke(created, erwartet);
        }

        _log?.Invoke($"{directories.Count - 1} Verzeichnisse, {created} Platzhalter angelegt (0 Bytes belegt).");
    }

    /// <summary>
    /// Legt einen einzelnen Platzhalter an. Fehlende Elternordner entstehen
    /// dabei mit.
    /// </summary>
    /// <remarks>
    /// Fuer Eintraege, die erst nach dem Verbinden dazukommen. Der grosse Lauf
    /// beim Start gruppiert nach Ordner, weil CfAPI stapelweise arbeitet; fuer
    /// einen Eintrag lohnt das nicht.
    /// </remarks>
    public bool CreatePlaceholder(VirtualEntry entry)
    {
        var parent = ParentOf(entry.RelativePath);

        var directory = string.IsNullOrEmpty(parent)
            ? _rootPath
            : Path.Combine(_rootPath, parent.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(directory);

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(
                Path.Combine(_rootPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            return true;
        }

        return CreatePlaceholders(parent, [entry]) == 1;
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
                // Die Identitaet kommt im Rueckruf zurueck. Sie enthaelt den
                // vollen relativen Pfad, damit die Datei dort ohne Umweg
                // gefunden wird.
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
                    // befuellt. Da die enthaltenen Eintraege erst danach
                    // angelegt werden, wertet Windows das als Widerspruch und
                    // liefert fuer den ganzen Stapel
                    // ERROR_CLOUD_FILE_METADATA_CORRUPT. Dass Windows
                    // trotzdem nicht nach Population fragt, regelt die
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

            // CfAPI haelt beim ersten fehlerhaften Eintrag an. Nicht
            // bearbeitete Eintraege behalten Result = S_OK und sind damit
            // nicht von erfolgreichen zu unterscheiden. Nur EntriesProcessed
            // gibt an, wie weit der Aufruf kam.
            if (processed < entries.Count)
            {
                var stuck = entries[(int)processed];
                var code = (uint)infos[(int)processed].Result.Value;
                _log?.Invoke($"  ABBRUCH in \"{directory}\" nach {processed}/{entries.Count}: " +
                             $"\"{stuck.RelativePath}\" -> Eintrag 0x{code:X8}, " +
                             $"Aufruf 0x{(uint)callResult.Value:X8}");
            }

            // Windows leitet die Ueberlagerungssymbole (Wolke, Kringel,
            // gruener Haken) aus dem Anheft-Zustand ab. Ohne ihn zeigt es gar
            // keines. "Nicht angeheftet" bedeutet, dass der Speicherplatz der Datei freigegeben
            // werden darf. Das ist hier der Normalfall.
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


    private unsafe void MarkUnpinned(string fullPath) => SetPinned(fullPath, false);

    /// <summary>
    /// Setzt den Anheft-Zustand.
    /// </summary>
    /// <remarks>
    /// "Angeheftet" ist das Versprechen aus dem Kontextmenue von Windows:
    /// immer auf diesem Geraet behalten. Es wird eingehalten -- eine
    /// angeheftete Datei behaelt ihren Inhalt, auch wenn eine Grenze erreicht ist.
    ///
    /// "Nicht angeheftet" ist der Normalfall dieser Freigabe. Windows leitet
    /// daraus auch die Ueberlagerungssymbole ab: ohne Anheft-Zustand zeigt es
    /// gar keines.
    /// </remarks>
    public unsafe void SetPinned(string fullPath, bool pinned)
    {
        try
        {
            using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var result = PInvoke.CfSetPinState(
                handle,
                pinned ? CF_PIN_STATE.CF_PIN_STATE_PINNED : CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
                CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE, null);

            if (result.Failed)
                _log?.Invoke($"  Anheft-Zustand fuer \"{Path.GetFileName(fullPath)}\": 0x{(uint)result.Value:X8}");
        }
        catch (IOException) { /* in Benutzung, beim naechsten Start erneut */ }
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
            // wird kopiert, bevor die Verarbeitung asynchron weiterlaeuft.
            //
            // Der Ausloeser wird mitgefuehrt: bei voller Hydration trotz
            // PARTIAL-Politik ist zuerst zu klaeren, ob der Benutzer den
            // Zugriff ausgeloest hat. Ein Virenscanner liest die Datei beim
            // Oeffnen vollstaendig.
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

            // Der Rueckruf darf nicht blockieren. Windows wartet sonst auf
            // diesen Aufruf, waehrend dieser auf das Netz wartet.
            _ = Task.Run(() => mount.ServeAsync(request));
        }
        catch
        {
            // Aus einem UnmanagedCallersOnly-Rueckruf darf keine Ausnahme
            // herausdringen.
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
        // Windows verlangt einen sektorausgerichteten Anfang. Die Laenge darf
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

            // Stueckweise, nicht in einem Zug. Dafuer gibt es zwei gemessene
            // Gruende.
            //
            // Windows fragt fuer dieselbe Datei mehrfach und in
            // ueberlappenden Bereichen an. Ein Dateimanager verlangt beim
            // Doppelklick die ganze Datei, der Lader gleich darauf noch
            // einmal fast dasselbe. Wird erst am Ende durchgereicht, faellt
            // erst am Ende auf, dass ein anderer Abruf den Bereich laengst
            // gefuellt hat. Bei 373 MB bedeutet das die doppelte
            // Uebertragung. Nach jedem Stueck steht dagegen fest, ob der
            // Bereich noch gebraucht wird.
            //
            // Zweitens der Speicher: eine Datei in einem Zug zu holen hiesse,
            // sie vollstaendig im Arbeitsspeicher zu halten.
            //
            // Der ganze Bereich ist eine Uebertragung, auch wenn er in
            // Stuecken kommt.
            using var range = _source.BeginRange(request.RelativePath, length);

            for (var offset = start; offset < alignedEnd;)
            {
                var take = (int)Math.Min(ChunkSize, alignedEnd - offset);

                var data = await _source.ReadAsync(request.RelativePath, offset, take, CancellationToken.None)
                    .ConfigureAwait(false);

                if (!TransferData(request, data, offset)) return;

                offset += take;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Hydration fehlgeschlagen fuer {request.RelativePath}: {ex.Message}");
            // STATUS_UNSUCCESSFUL. Der Zugriff scheitert, statt
            // haengenzubleiben.
            TransferFailure(request, start);
        }
    }

    /// <summary>
    /// Kopiert die Nutzdaten in sektorausgerichteten Speicher und reicht sie
    /// durch. <c>false</c> bedeutet, dass die Anfrage erledigt ist und nicht
    /// weiter bedient werden muss.
    /// </summary>
    private unsafe bool TransferData(HydrationRequest request, byte[] data, long offset)
    {
        var buffer = NativeMemory.AlignedAlloc((nuint)data.Length, SectorSize);
        try
        {
            data.AsSpan().CopyTo(new Span<byte>(buffer, data.Length));
            return Transfer(request, buffer, offset, data.Length, ntStatus: 0);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>
    /// So viel wird am Stueck geholt und durchgereicht. Der Wert ist gross
    /// genug, dass die Verbindung ausgelastet bleibt, und klein genug, dass
    /// eine ueberfluessige Anfrage schnell erkannt wird. Er ist ein
    /// Vielfaches der Sektorgroesse.
    /// </summary>
    private const int ChunkSize = 8 << 20;

    private unsafe void TransferFailure(HydrationRequest request, long offset)
        => Transfer(request, null, offset, 0, unchecked((int)0xC0000001));

    private unsafe bool Transfer(HydrationRequest request, void* buffer, long offset, long length, int ntStatus)
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
        if (!result.Failed) return true;

        // 0x8007018E: die Anfrage wurde storniert, weil ein anderer Abruf den
        // Bereich bereits gefuellt hat. Das ist kein Fehler, aber ein Grund
        // abzubrechen. Alles Weitere waere eine zweite Uebertragung derselben
        // Bytes.
        const uint requestCanceled = 0x8007018E;
        if ((uint)result.Value == requestCanceled)
            _log?.Invoke("  (Anfrage storniert -- Bereich war bereits gefuellt)");
        else
            _log?.Invoke($"  CfExecute schlug fehl: 0x{(uint)result.Value:X8}");

        return false;
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

    // ------------------------------------------------------------ Meldungen

    // Fuer die drei Melde-Rueckrufe gelten dieselben Regeln wie fuer
    // OnFetchData: die Zeiger sind nur waehrend des Aufrufs gueltig, es darf
    // keine Ausnahme herausdringen, und der Rueckruf darf nicht blockieren.
    // Deshalb werden die Pfade sofort kopiert und die Ereignisse auf einem
    // anderen Thread ausgeloest.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFileCloseCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var mount = (CloudFilterMount)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;
            var path = mount.ToRelativePath(info->NormalizedPath.Value);
            if (path.Length == 0) return;

            mount.Raise(() => mount.FileClosed?.Invoke(path), "FileClosed", path);
        }
        catch
        {
            // Aus einem UnmanagedCallersOnly-Rueckruf darf keine Ausnahme
            // herausdringen.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnDeleteCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var mount = (CloudFilterMount)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;
            var path = mount.ToRelativePath(info->NormalizedPath.Value);
            if (path.Length == 0) return;

            mount.Raise(() => mount.FileDeleted?.Invoke(path), "FileDeleted", path);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnRenameCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var mount = (CloudFilterMount)GCHandle.FromIntPtr((nint)info->CallbackContext).Target!;

            // Der alte Pfad steht in den Parametern, der neue in
            // NormalizedPath. Beide sind volumenrelativ, ohne Laufwerksbuchstabe.
            var oldPath = mount.ToRelativePath(parameters->Anonymous.RenameCompletion.SourcePath.Value);
            var newPath = mount.ToRelativePath(info->NormalizedPath.Value);
            if (oldPath.Length == 0 && newPath.Length == 0) return;

            mount.Raise(() => mount.FileRenamed?.Invoke(oldPath, newPath),
                        "FileRenamed", $"{oldPath} -> {newPath}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Loest ein Ereignis ausserhalb des Rueckrufs aus. Windows wartet auf die
    /// Rueckkehr des Rueckrufs, deshalb darf dort nichts laufen, was Zeit
    /// braucht.
    /// </summary>
    private void Raise(Action raise, string what, string subject)
    {
        _ = Task.Run(() =>
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{what} fuer {subject} fehlgeschlagen: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------ Hilfsmittel

    /// <summary>
    /// Wandelt einen Pfad der Cloud-Files-Schicht in die Form um, die der Rest
    /// benutzt: relativ zur Freigabe, mit / als Trenner, ohne fuehrenden
    /// Trenner.
    /// </summary>
    /// <remarks>
    /// Die Schicht liefert volumenrelative Pfade ohne Laufwerksbuchstabe, also
    /// etwa <c>\Users\name\Freigabe\Ordner\Datei.txt</c>. Liegt der Pfad
    /// ausserhalb der Freigabe -- etwa das Ziel einer Verschiebung heraus --,
    /// kommt eine leere Zeichenkette zurueck.
    /// </remarks>
    private unsafe string ToRelativePath(char* path)
    {
        if (path is null) return string.Empty;

        var text = Marshal.PtrToStringUni((nint)path);
        if (string.IsNullOrEmpty(text)) return string.Empty;

        text = StripDriveLetter(text);

        if (!text.StartsWith(_volumeRelativeRoot, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var rest = text[_volumeRelativeRoot.Length..];

        // Der Rest muss an einer Trennstelle beginnen, sonst ist es ein
        // Nachbarordner mit gleichem Anfang.
        if (rest.Length > 0 && rest[0] is not ('\\' or '/')) return string.Empty;

        return rest.Replace('\\', '/').TrimStart('/');
    }

    private static string StripDriveLetter(string path)
        => path.Length >= 2 && path[1] == ':' ? path[2..] : path;

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
