using ProtoBuf;

namespace Gf2ExiliumTranslationLauncher.Translations.Protobuf;

[ProtoContract]
public sealed class TIndex
{
    [ProtoMember(1)]
    public uint dist { get; set; }

    [ProtoMember(2)]
    public uint sect { get; set; }

    [ProtoMember(3)]
    public Dictionary<uint, TIndexItem> items { get; set; } = new();
}

[ProtoContract]
public sealed class TIndexItem
{
    [ProtoMember(1)]
    public uint beginOff { get; set; }

    [ProtoMember(2)]
    public uint length { get; set; }
}

[ProtoContract]
public sealed class TranslationChunk
{
    [ProtoMember(1)]
    public List<TranslationRow> data { get; set; } = [];
}

[ProtoContract]
public sealed class TranslationRow
{
    [ProtoMember(1)]
    public uint id { get; set; }

    [ProtoMember(2)]
    public string? text { get; set; }
}

public sealed record TranslationTableRow(uint Id, string Text);
