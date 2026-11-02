namespace BTokenCore;


public class TXOutputTokenAnchor : TXOutput
{
  internal const int LENGTH_ID_TOKEN = 4;

  internal static byte[] IDENTIFIER_BTOKEN_PROTOCOL = [(byte)'B', (byte)'T', (byte)'K'];

  internal const byte OP_RETURN = 0x6A;
  internal static readonly byte LengthDataAnchorToken =
    (byte)(IDENTIFIER_BTOKEN_PROTOCOL.Length + LENGTH_ID_TOKEN + 32 + 32);

  internal static byte[] PREFIX_ANCHOR_TOKEN =
    new byte[] { OP_RETURN, LengthDataAnchorToken }.Concat(IDENTIFIER_BTOKEN_PROTOCOL).ToArray();

  internal readonly static int LENGTH_SCRIPT_ANCHOR_TOKEN =
    PREFIX_ANCHOR_TOKEN.Length + LENGTH_ID_TOKEN + 32 + 32;

  internal byte[] IDToken = new byte[LENGTH_ID_TOKEN];

  internal byte[] HashBlockReferenced = new byte[32];
  internal byte[] HashBlockPreviousReferenced = new byte[32];


  internal TXOutputTokenAnchor()
  {
  }

  internal TXOutputTokenAnchor(byte[] buffer, ref int startIndex)
  {
    startIndex += PREFIX_ANCHOR_TOKEN.Length;

    IDToken = buffer.ReadBytes(ref startIndex, LENGTH_ID_TOKEN);

    HashBlockReferenced = buffer.ReadBytes(ref startIndex, 32);

    HashBlockPreviousReferenced = buffer.ReadBytes(ref startIndex, 32);
  }

  internal void Serialize()
  {
    Script = [.. PREFIX_ANCHOR_TOKEN, .. IDToken, .. HashBlockReferenced, .. HashBlockPreviousReferenced];
  }
}
