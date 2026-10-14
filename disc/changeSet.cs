namespace DotrAssetModder;

/* Importers never touch the disc. They describe what they want changed - a run of bytes at an
   offset inside one file on the disc - and this turns that into disc bytes, or refuses.

   The disc is patched where it lies: every change must fit inside the byte size its file already
   has, because nothing here rewrites a directory record. */
public class ChangeSet
{
    // Offset is inside the file, not inside the image. Source is free text for the validation
    // report: "pack 23 sub-model 4", "anOfs_MODEL".
    public class Change
    {
        public string FilePath;
        public long Offset;
        public byte[] Bytes;
        public string Source;
        public long ImageOffset;   // set by Validate

        public long End
        {
            get { return Offset + Bytes.Length; }
        }

        public override string ToString()
        {
            return $"{FilePath}+0x{Offset:X} {Bytes.Length} B ({Source})";
        }
    }

    public class Plan
    {
        // Changes that will actually be written, with ImageOffset resolved.
        public List<Change> Writes = new List<Change>();

        // Changes whose bytes already equal what is on the disc. Skipped, never rewritten.
        public List<Change> NoOps = new List<Change>();

        // One line per change that cannot be written, saying by how much it overflows.
        public List<string> Errors = new List<string>();

        public bool Ok
        {
            get { return Errors.Count == 0; }
        }

        public long BytesToWrite
        {
            get
            {
                long total = 0;
                foreach (Change change in Writes)
                {
                    total += change.Bytes.Length;
                }
                return total;
            }
        }
    }

    public List<Change> Changes = new List<Change>();

    public int Count
    {
        get { return Changes.Count; }
    }

    public void Add(string filePath, long offset, byte[] bytes, string source = "")
    {
        var change = new Change();
        change.FilePath = filePath;
        change.Offset = offset;
        change.Bytes = bytes;
        change.Source = source;
        Changes.Add(change);
    }

    // Replace a whole file's contents (still subject to the fit check).
    public void AddWholeFile(string filePath, byte[] bytes, string source)
    {
        Add(filePath, 0, bytes, source);
    }

    public void Clear()
    {
        Changes.Clear();
    }

    // Reports every problem, not just the first, and reads the disc to find the no-ops. Nothing
    // is written here.
    public Plan Validate(FileLoader disc)
    {
        var plan = new Plan();

        foreach (Change change in Changes)
        {
            var discFile = disc.Find(change.FilePath);
            if (discFile == null)
            {
                plan.Errors.Add($"{change}: no such file on the disc");
                continue;
            }

            if (change.End > discFile.Size)
            {
                long overflowBytes = change.End - discFile.Size;
                string spareRoomNote = "; the extent has no spare sectors";
                if (discFile.Capacity > discFile.Size)
                {
                    spareRoomNote = $"; the extent has {discFile.Capacity - discFile.Size} spare bytes, but using them needs the directory record rewritten (IsoRebuilder)";
                }
                plan.Errors.Add($"{change}: overflows {discFile.IsoPath} ({discFile.Size} B) by {overflowBytes} B{spareRoomNote}");
                continue;
            }

            if (change.Bytes.Length == 0)
            {
                plan.NoOps.Add(change);
                continue;
            }

            change.ImageOffset = discFile.ImageOffset + change.Offset;
            byte[] current = new byte[change.Bytes.Length];
            disc.ReadAt(change.ImageOffset, current, 0, current.Length);
            if (current.AsSpan().SequenceEqual(change.Bytes))
            {
                plan.NoOps.Add(change);
                continue;
            }

            plan.Writes.Add(change);
        }

        // Two importers writing the same bytes differently is a bug in the caller, not something
        // to resolve by write order.
        List<Change> sorted = plan.Writes.OrderBy(plannedWrite => plannedWrite.ImageOffset).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].ImageOffset < sorted[i - 1].ImageOffset + sorted[i - 1].Bytes.Length)
            {
                plan.Errors.Add($"{sorted[i]}: overlaps {sorted[i - 1]} in the image");
            }
        }

        return plan;
    }

    // Validates first and writes nothing at all if anything fails. Returns the plan so the caller
    // can report the no-ops and the byte count.
    public Plan Write(FileLoader disc)
    {
        Plan plan = Validate(disc);
        if (!plan.Ok || plan.Writes.Count == 0)
        {
            return plan;
        }

        using (var imageStream = new FileStream(disc.ImagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            foreach (Change change in plan.Writes)
            {
                imageStream.Seek(change.ImageOffset, SeekOrigin.Begin);
                imageStream.Write(change.Bytes, 0, change.Bytes.Length);
            }
            imageStream.Flush(true);
        }
        return plan;
    }
}
