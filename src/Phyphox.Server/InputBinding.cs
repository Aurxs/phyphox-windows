// SPDX-License-Identifier: GPL-3.0-only
using Phyphox.Devices;
namespace Phyphox.Server;
public sealed record InputMapping(string Buffer, string Conversion, int Offset = 0, int Repeating = 0, int Size = 0, double Scale = 1, double Bias = 0);
public sealed record InputBinding(string ConnectionId, InputMapping[] Mappings, int? InputIndex = null, string? TimeBuffer = null,
    int PacketLength = 0, string? DelimiterHex = null, bool AllowSourceReplacement = false);
internal sealed class ActiveInputBinding(InputBinding definition, PacketFramer? framer)
{
    public InputBinding Definition { get; } = definition;
    public PacketFramer? Framer { get; } = framer;
}
