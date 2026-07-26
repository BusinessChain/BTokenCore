using System;
using System.Linq;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public class TXOutputTokenAnchor : TXOutput
    {
      public static byte[] IDENTIFIER_BTOKEN_PROTOCOL = new byte[] { (byte)'B', (byte)'T', (byte)'K' };

      public const byte OP_RETURN = 0x6A;
      public const byte LengthDataAnchorToken = 70;

      public static byte[] PREFIX_ANCHOR_TOKEN =
        new byte[] { OP_RETURN, LengthDataAnchorToken }.Concat(IDENTIFIER_BTOKEN_PROTOCOL).ToArray();

      public readonly static int LENGTH_SCRIPT_ANCHOR_TOKEN =
        PREFIX_ANCHOR_TOKEN.Length + LENGTH_IDTOKEN + 32 + 32;

      public const int LENGTH_IDTOKEN = 4;
      public byte[] IDToken = new byte[LENGTH_IDTOKEN];

      public byte[] HashBlockReferenced = new byte[32];
      public byte[] HashBlockPreviousReferenced = new byte[32];

      byte[] TXOutputTokenAnchorRaw = new byte[IDENTIFIER_BTOKEN_PROTOCOL.Length + LENGTH_IDTOKEN + 32 + 32];


      public TXOutputTokenAnchor()
      { 
      }

      public TXOutputTokenAnchor(byte[] buffer, ref int startIndex)
      {
        startIndex += PREFIX_ANCHOR_TOKEN.Length;

        Array.Copy(buffer, startIndex, IDToken, 0, LENGTH_IDTOKEN);
        startIndex += LENGTH_IDTOKEN;

        Array.Copy(buffer, startIndex, HashBlockReferenced, 0, HashBlockReferenced.Length);
        startIndex += HashBlockReferenced.Length;

        Array.Copy(buffer, startIndex, HashBlockPreviousReferenced, 0, HashBlockPreviousReferenced.Length);
        startIndex += HashBlockPreviousReferenced.Length;
      }

      public byte[] Serialize()
      {
        int startIndex = 0;

        IDENTIFIER_BTOKEN_PROTOCOL.CopyTo(TXOutputTokenAnchorRaw, startIndex);

        startIndex += IDENTIFIER_BTOKEN_PROTOCOL.Length;

        IDToken.CopyTo(TXOutputTokenAnchorRaw, startIndex);

        startIndex += LENGTH_IDTOKEN;

        HashBlockReferenced.CopyTo(TXOutputTokenAnchorRaw, startIndex);

        startIndex += 32;

        HashBlockPreviousReferenced.CopyTo(TXOutputTokenAnchorRaw, startIndex);

        return TXOutputTokenAnchorRaw;
      }
    }
  }
}
