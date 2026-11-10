using System.Buffers.Binary;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Localization;
using ProtoBuf;

namespace Gf2ExiliumTranslationLauncher.Translations.Protobuf;

public sealed class TranslationProtobufWriter
{
    public Task WriteRowsAsync(
        string filePath,
        IEnumerable<TranslationTableRow> rows,
        CancellationToken cancellationToken = default)
    {
        const uint distance = 10;
        return WriteGroupedRowsAsync(filePath, rows, distance, 1, row => row.Id / distance, cancellationToken);
    }

    internal Task WriteSingleChunkAsync(
        string filePath,
        IEnumerable<TranslationTableRow> rows,
        uint distance,
        CancellationToken cancellationToken)
        => WriteGroupedRowsAsync(filePath, rows, distance, 0, _ => 0, cancellationToken);

    private async Task WriteGroupedRowsAsync(
        string filePath,
        IEnumerable<TranslationTableRow> rows,
        uint distance,
        uint section,
        Func<TranslationTableRow, uint> getChunkKey,
        CancellationToken cancellationToken)
    {
        AppLog.Info($"Writing translation table rows. File={AppLog.DescribePath(filePath)}");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            AppLog.Warn("Cannot write translation table rows: output file path is empty.");
            throw new ArgumentException(AppText.Get("ErrorOutputFilePathRequired"), nameof(filePath));
        }

        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();


        var orderedRows = rows.OrderBy(x => x.Id).ToList();
        AppLog.Info($"Preparing protobuf table. File={filePath}, Rows={orderedRows.Count}");

        var index = new TIndex
        {
            dist = distance,
            sect = section
        };

        var chunkBlobs = new List<byte[]>();
        uint runningOffset = 0;

        foreach (var group in orderedRows.GroupBy(getChunkKey))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkRows = group.Select(entry => new TranslationRow
            {
                id = entry.Id,
                text = entry.Text
            }).ToList();

            var chunkBytes = Serialize(new TranslationChunk { data = chunkRows });

            index.items[group.Key] = new TIndexItem
            {
                beginOff = runningOffset,
                length = (uint)chunkBytes.Length
            };

            chunkBlobs.Add(chunkBytes);
            runningOffset = checked(runningOffset + (uint)chunkBytes.Length);
        }

        var indexBytes = Serialize(index);
        AppLog.Info($"Serialized protobuf table. File={filePath}, Chunks={chunkBlobs.Count}, IndexBytes={indexBytes.Length}, DataBytes={runningOffset}");

        var outputDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await using var outputStream = File.Create(filePath);
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)indexBytes.Length);

        await outputStream.WriteAsync(header, cancellationToken);
        await outputStream.WriteAsync(indexBytes, cancellationToken);

        foreach (var chunk in chunkBlobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await outputStream.WriteAsync(chunk, cancellationToken);
        }

        AppLog.Info($"Translation table written. File={filePath}, Rows={orderedRows.Count}, Chunks={chunkBlobs.Count}");
    }

    public async Task<IReadOnlyList<TranslationTableRow>> ReadRowsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        AppLog.Info($"Reading translation table rows. File={AppLog.DescribePath(filePath)}");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            AppLog.Warn("Cannot read translation table: input file path is empty.");
            throw new ArgumentException(AppText.Get("ErrorInputFilePathRequired"), nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            AppLog.Warn($"Cannot read translation table: file not found. File={filePath}");
            throw new FileNotFoundException(AppText.Format("ErrorFileNotFound", filePath), filePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var streamOptions = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        await using var inputStream = new FileStream(filePath, streamOptions);
        var header = new byte[sizeof(uint)];
        await inputStream.ReadExactlyAsync(header, cancellationToken);

        var indexLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (indexLength > inputStream.Length - inputStream.Position)
        {
            AppLog.Warn($"Invalid translation table index length. File={filePath}, IndexLength={indexLength}, FileLength={inputStream.Length}");
            throw new InvalidDataException(AppText.Format("ErrorTableIndexLengthInvalid", filePath));
        }

        var indexBytes = new byte[ToBufferLength(indexLength, "index")];
        await inputStream.ReadExactlyAsync(indexBytes, cancellationToken);

        var index = Deserialize<TIndex>(indexBytes);
        var dataStart = inputStream.Position;
        var rows = new List<TranslationTableRow>();

        foreach (var item in index.items.OrderBy(x => x.Key).Select(x => x.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkLength = ToBufferLength(item.length, "chunk");
            var chunkStart = checked(dataStart + item.beginOff);
            var chunkEnd = checked(chunkStart + chunkLength);
            if (chunkStart < dataStart || chunkEnd > inputStream.Length)
            {
                AppLog.Warn($"Invalid translation table chunk offset. File={filePath}, ChunkStart={chunkStart}, ChunkLength={chunkLength}, FileLength={inputStream.Length}");
                throw new InvalidDataException(AppText.Format("ErrorTableChunkOffsetInvalid", filePath));
            }

            inputStream.Seek(chunkStart, SeekOrigin.Begin);
            var chunkBytes = new byte[chunkLength];
            await inputStream.ReadExactlyAsync(chunkBytes, cancellationToken);

            var chunk = Deserialize<TranslationChunk>(chunkBytes);
            rows.AddRange(chunk.data.Select(row => new TranslationTableRow(row.id, row.text ?? string.Empty)));
        }

        var orderedRows = rows.OrderBy(x => x.Id).ToList();
        AppLog.Info($"Translation table read. File={filePath}, Rows={orderedRows.Count}, Chunks={index.items.Count}");
        return orderedRows;
    }

    private static byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, value);
        return stream.ToArray();
    }

    private static T Deserialize<T>(byte[] value)
    {
        using var stream = new MemoryStream(value);
        return Serializer.Deserialize<T>(stream);
    }

    private static int ToBufferLength(uint length, string label)
    {
        if (length > int.MaxValue)
        {
            AppLog.Warn($"Translation table {label} is too large. Length={length}");
            throw new InvalidDataException(AppText.Format("ErrorTableTooLarge", label, length));
        }

        return (int)length;
    }
}
