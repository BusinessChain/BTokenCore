using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  internal const string NAME_TOKEN = "TokenBitcoin";


  internal BlockchainBitcoin(LiteDatabase liteDatabase, SemaphoreSlim semaphoreBlockchain)
    : base(NAME_TOKEN, liteDatabase, SIZE_BLOCK_MAX, new ParserBitcoin(), blockchainParent: null, semaphoreBlockchain)
  { }

  protected override Header CreateHeaderRoot()
  {
    HeaderBitcoin header = new(
       headerHash: "000000000000000000001e39df127cbab82a824ac43cfcdbf62e59f6a08b9f0b".ToBinary(),
       version: 0x2000e000,
       hashPrevious: "00000000000000000001438062096e4055a0de69e2a56a3b69d3be5a140974ea".ToBinary(),
       merkleRootHash: "933a6ea902765147f88538292bb11ef0afc77fe368c3fd1845aaf368c40cf4a8".ToBinary(),
       unixTimeSeconds: 1791011796,
       nBits: 386014960,
       nonce: 161796985);

    header.Height = 969696;

    return header;
  }

  internal override long GetSecondsAgeHeader(int height)
  {
    return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ((HeaderBitcoin)ChainRoot.HeaderTip.GetHeaderAncestor(height)).UnixTimeSeconds;
  }
}