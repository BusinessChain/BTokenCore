namespace BTokenCore;


public class TXOutputTokenAnchor : TXOutput
{
  internal const int LENGTH_ID_TOKEN = 4;

  internal const int LENGTH_SCRIPT_ANCHOR_TOKEN = 5 + LENGTH_ID_TOKEN + 2 * Crypto.LENGTH_HASH256;

  internal static readonly byte[] PREFIX_ANCHOR_TOKEN =
    [0x6A, LENGTH_SCRIPT_ANCHOR_TOKEN - 2, (byte)'B', (byte)'T', (byte)'K'];

  internal byte[] IDToken;

  internal byte[] HashBlockReferenced;
  internal byte[] HashBlockPreviousReferenced;


  internal TXOutputTokenAnchor()
  {
  }

  internal TXOutputTokenAnchor(byte[] buffer, ref int startIndex)
  {
    startIndex += PREFIX_ANCHOR_TOKEN.Length;

    IDToken = buffer.ReadBytes(ref startIndex, LENGTH_ID_TOKEN);

    HashBlockReferenced = buffer.ReadBytes(ref startIndex, Crypto.LENGTH_HASH256);

    HashBlockPreviousReferenced = buffer.ReadBytes(ref startIndex, Crypto.LENGTH_HASH256);
  }

  internal void Serialize()
  {
    Script = [.. PREFIX_ANCHOR_TOKEN, .. IDToken, .. HashBlockReferenced, .. HashBlockPreviousReferenced];
  }
}
