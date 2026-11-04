using System.Text;
using PulseAPK.Core.Services.Patching;

namespace PulseAPK.Tests.Services.Patching;

public class DexMethodLookupServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChecksClassAndMethodBelongToTheSameReference(bool targetOwnsMethod)
    {
        var dex = CreateDex(targetOwnsMethod);
        var found = new DexMethodLookupService().ContainsMethodReference(dex, "Lcom/example/Target;", "loadGadget", "()V");
        Assert.Equal(targetOwnsMethod, found);
    }

    [Fact]
    public void RejectsMalformedDexEvenWhenItContainsAllSearchTokens()
    {
        var dex = Encoding.UTF8.GetBytes("Lcom/example/Target; loadGadget ()V");
        Assert.ThrowsAny<Exception>(() => new DexMethodLookupService().ContainsMethodReference(dex, "Lcom/example/Target;", "loadGadget", "()V"));
    }

    private static byte[] CreateDex(bool targetOwnsMethod)
    {
        // The unrelated literal ()V ensures a raw substring heuristic is insufficient.
        var strings = new[] { "()V", "Lcom/example/Other;", "Lcom/example/Target;", "V", "loadGadget" };
        var encoded = strings.Select(s => new byte[] { checked((byte)s.Length) }.Concat(Encoding.UTF8.GetBytes(s)).Append((byte)0).ToArray()).ToArray();
        const int header = 0x70;
        var stringsOffset = header;
        var typesOffset = stringsOffset + strings.Length * 4;
        var protosOffset = typesOffset + 3 * 4;
        var methodsOffset = protosOffset + 12;
        var dataOffset = methodsOffset + 8;
        var dataEnd = dataOffset + encoded.Sum(x => x.Length);
        var mapOffset = (dataEnd + 3) & ~3;
        const int mapCount = 7;
        var bytes = new byte[mapOffset + 4 + mapCount * 12];
        void Write32(int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        void Write16(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        Encoding.ASCII.GetBytes("dex\n035\0").CopyTo(bytes, 0);
        Write32(0x20, bytes.Length); Write32(0x24, header); Write32(0x28, 0x12345678);
        Write32(0x34, mapOffset); Write32(0x38, strings.Length); Write32(0x3c, stringsOffset);
        Write32(0x40, 3); Write32(0x44, typesOffset); Write32(0x48, 1); Write32(0x4c, protosOffset);
        Write32(0x58, 1); Write32(0x5c, methodsOffset); Write32(0x68, bytes.Length - dataOffset); Write32(0x6c, dataOffset);
        var cursor = dataOffset;
        for (var i = 0; i < encoded.Length; i++) { Write32(stringsOffset + i * 4, cursor); encoded[i].CopyTo(bytes, cursor); cursor += encoded[i].Length; }
        Write32(typesOffset, 1); Write32(typesOffset + 4, 2); Write32(typesOffset + 8, 3);
        Write32(protosOffset, 3); Write32(protosOffset + 4, 2); Write32(protosOffset + 8, 0);
        Write16(methodsOffset, (ushort)(targetOwnsMethod ? 1 : 0)); Write16(methodsOffset + 2, 0); Write32(methodsOffset + 4, 4);
        Write32(mapOffset, mapCount);
        void Map(int index, ushort type, int count, int offset) { var at = mapOffset + 4 + index * 12; Write16(at, type); Write32(at + 4, count); Write32(at + 8, offset); }
        Map(0, 0, 1, 0); Map(1, 1, strings.Length, stringsOffset); Map(2, 2, 3, typesOffset);
        Map(3, 3, 1, protosOffset); Map(4, 5, 1, methodsOffset); Map(5, 0x2002, strings.Length, dataOffset); Map(6, 0x1000, 1, mapOffset);
        return bytes;
    }
}
