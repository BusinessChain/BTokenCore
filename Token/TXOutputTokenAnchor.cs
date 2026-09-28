namespace BTokenCore;

public class TXOutputTokenAnchor : TXOutput
{
  internal static byte[] IDENTIFIER_BTOKEN_PROTOCOL = new byte[] { (byte)'B', (byte)'T', (byte)'K' };

  internal const byte OP_RETURN = 0x6A;
  internal const byte LengthDataAnchorToken = 70;

  internal static byte[] PREFIX_ANCHOR_TOKEN =
    new byte[] { OP_RETURN, LengthDataAnchorToken }.Concat(IDENTIFIER_BTOKEN_PROTOCOL).ToArray();

  internal readonly static int LENGTH_SCRIPT_ANCHOR_TOKEN =
    PREFIX_ANCHOR_TOKEN.Length + Token.LENGTH_ID_TOKEN + 32 + 32;

  internal byte[] IDToken = new byte[Token.LENGTH_ID_TOKEN];

  internal byte[] HashBlockReferenced = new byte[32];
  internal byte[] HashBlockPreviousReferenced = new byte[32];

  byte[] TXOutputTokenAnchorRaw = new byte[IDENTIFIER_BTOKEN_PROTOCOL.Length + Token.LENGTH_ID_TOKEN + 32 + 32];


  internal TXOutputTokenAnchor()
  {
  }

  internal TXOutputTokenAnchor(byte[] buffer, ref int startIndex)
  {
    startIndex += PREFIX_ANCHOR_TOKEN.Length;

    Array.Copy(buffer, startIndex, IDToken, 0, Token.LENGTH_ID_TOKEN);
    startIndex += Token.LENGTH_ID_TOKEN;

    Array.Copy(buffer, startIndex, HashBlockReferenced, 0, HashBlockReferenced.Length);
    startIndex += HashBlockReferenced.Length;

    Array.Copy(buffer, startIndex, HashBlockPreviousReferenced, 0, HashBlockPreviousReferenced.Length);
    startIndex += HashBlockPreviousReferenced.Length;
  }

  internal byte[] Serialize()
  {
    int startIndex = 0;

    IDENTIFIER_BTOKEN_PROTOCOL.CopyTo(TXOutputTokenAnchorRaw, startIndex);

    startIndex += IDENTIFIER_BTOKEN_PROTOCOL.Length;

    IDToken.CopyTo(TXOutputTokenAnchorRaw, startIndex);

    startIndex += Token.LENGTH_ID_TOKEN;

    HashBlockReferenced.CopyTo(TXOutputTokenAnchorRaw, startIndex);

    startIndex += 32;

    HashBlockPreviousReferenced.CopyTo(TXOutputTokenAnchorRaw, startIndex);

    return TXOutputTokenAnchorRaw;
  }
}
