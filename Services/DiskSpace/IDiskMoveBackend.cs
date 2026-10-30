namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// Every disk operation the <see cref="DiskMoveRunner"/> performs. <see cref="WindowsDiskMoveBackend"/> is the
/// real one; unit tests use a fake that models the disk. Partitions are always named by their GPT partition GUID
/// and disks by their current number (the runner finds it from the disk GUID); failures throw
/// <see cref="DiskMoveException"/> (or a subclass).
/// </summary>
public interface IDiskMoveBackend
{
    /// <summary>Current number of the disk with this GPT disk GUID; throws <see cref="DiskMoveException"/> if none.</summary>
    int FindDiskNumber(Guid diskGuid);

    /// <summary>Actual start (bytes) of the partition read from the partition table; throws if it is not on the disk.</summary>
    long PartitionStart(int disk, Guid part);
    /// <summary>Actual length (bytes) of the partition read from the partition table.</summary>
    long PartitionSize(int disk, Guid part);
    /// <summary>MSFT_Partition.Resize (shrinks or extends the end). No-op if the size already is newSize.</summary>
    void ResizePartition(int disk, Guid part, long newSize);

    /// <summary>Exact GPT attribute bits (DiskMove.dll).</summary>
    ulong GetPartitionAttributes(int disk, Guid part);
    /// <summary>Sets the exact GPT attribute bits in one layout write (DiskMove.dll; no-op if unchanged).</summary>
    void SetPartitionAttributes(int disk, Guid part, ulong attributes);

    /// <summary>Removes the drive letter of the volume; succeeds if Windows already removed it.</summary>
    void RemoveLetter(string volumePath, char letter);
    /// <summary>Gives the volume its letter back (succeeds if Windows already did; drops letters Windows auto-assigned).
    /// If another volume holds that letter, keeps the letter Windows auto-assigned or assigns the first free one
    /// from D..Z. Returns the letter the volume has now; throws only if it can get none.</summary>
    char AssignLetter(string volumePath, char letter);
    /// <summary>First drive letter of the volume, or null (read-only).</summary>
    char? GetLetter(string volumePath);

    /// <summary>Opens the volume and takes FSCTL_LOCK_VOLUME; dispose to release. Throws if in use or absent.</summary>
    IDisposable LockVolume(string volumePath);
    /// <summary>True only if the locked volume is exactly [start, start + length) on the disk.</summary>
    bool VerifyVolume(IDisposable volume, int disk, long start, long length);
    void Dismount(IDisposable volume);

    /// <summary>WinRE state: ReAgent.xml InstallState, else the English "Windows RE status" of reagentc /info; null if unknown.</summary>
    bool? WinReEnabled();
    /// <summary>reagentc /enable or /disable (no-op if already so); throws <see cref="DiskMoveException"/> if WinRE
    /// does not end in that state.</summary>
    void SetWinRe(bool enabled);

    /// <summary>First 34 and last 33 sectors of the disk (primary and backup GPT).</summary>
    byte[] ReadRawGpt(int disk, int sectorSize);

    /// <summary>Read-only: the partition's first and last logical sector (NTFS boot and backup boot sector),
    /// <paramref name="sectorSize"/> bytes each, for the journal (v2). Call it after the volume is locked + dismounted
    /// (or hidden) and before any write.</summary>
    (byte[] First, byte[] Backup) ReadBootSectors(int disk, long start, long length, int sectorSize);

    /// <summary>Creates the journal (CREATE_ALWAYS) with the moves and the restore state as its app blob.</summary>
    void CreateJournal(string path, IReadOnlyList<NativeMovePlan> moves, RestoreState state);
    /// <summary>Null if there is no journal file; throws if it exists but cannot be read.</summary>
    JournalInfo? ReadJournal(string path, out RestoreState? state);
    void SetJournalStep(string path, MoveStep step);
    void DeleteJournal(string path);

    /// <summary>Runs or resumes move <paramref name="moveIndex"/> of the journal (DiskMove.dll checks the disk's
    /// identity first). <paramref name="progress"/>(done, total) returning true stops between two chunks.
    /// <paramref name="heldLock"/>: the caller's own lock on this partition's volume at its old start (locked +
    /// dismounted), or null; with null DiskMove.dll looks for the volume device still at the old offset (a hidden,
    /// half-moved partition keeps one) and holds it locked + dismounted for the whole run.
    /// Returns true when the move is complete, false when it stopped on request.</summary>
    bool RunMove(int disk, string journal, int moveIndex, Func<long, long, bool> progress, IDisposable? heldLock);
    /// <summary>Moves the GPT entry to newStart (idempotent).</summary>
    void SetPartitionStart(int disk, Guid part, long newStart);
    void PatchNtfsHiddenSectors(int disk, long start, long length, int sectorSize);

    /// <summary>Read-only "chkdsk X:"; returns its exit code (0 = clean).</summary>
    int Chkdsk(char letter);
}
