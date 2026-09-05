using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Reflection.Metadata.Ecma335;


namespace BTokenCore;

public abstract partial class Token : IToken
{
  internal const byte LENGTH_SCRIPT_P2PKH = 25;
  internal static byte[] PREFIX_P2PKH = [0x76, 0xA9, 0x14];
  internal static byte[] POSTFIX_P2PKH = [0x88, 0xAC];

  internal byte[] IDToken;
  internal Network Network;
  internal Wallet Wallet;

  internal ConcurrentBag<Block> PoolBlocks = new();

  internal int SizeBlockMax;

  bool IsLocked;


  internal int Port;
  internal UInt32 ProtocolVersion = 70015;
  internal ulong NetworkServicesLocal = 0;
  internal ulong NetworkServicesRemote = 0;
  internal string UserAgent = "/BTokenCore:0.0.0/";
  internal byte RelayOption = 0x01;


  protected Token()
  {
    Directory.CreateDirectory(GetName());

    Wallet = new Wallet(File.ReadAllText($"Wallet{GetName()}/wallet"));
  }

  public void Start()
  {
    Network.Start();
  }



  public void StartMiner()
  {
    Network.StartMiner();
  }

  public void StopMiner()
  {
    Network.StopMiner();
  }

  internal abstract string[] GetSeedAddresses();

  internal bool TryLock()
  {
    lock (this)
    {
      if (IsLocked)
        return false;

      IsLocked = true;
      return true;
    }
  }

  internal void ReleaseLock()
  {
    IsLocked = false;
  }

  internal int GetSizeBlockBuffer()
  {
    return SizeBlockMax;
  }

  internal Block GetBlock()
  {
    if (!PoolBlocks.TryTake(out Block block))
      block = new Block(this);

    return block;
  }

  internal void ReturnBlock(Block block)
  {
    PoolBlocks.Add(block);
  }

  public abstract Header CreateHeaderGenesis();

  internal abstract bool TryGetTX(byte[] hash, out TX tX);

  public abstract void InsertBlock(Block block);

  public virtual void ReverseBlock(Block block) { }

  public abstract Header ParseHeader(byte[] buffer, ref int index, SHA256 sHA256);

  public abstract TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase = false);

  internal string GetName()
  {
    return GetType().Name;
  }

  internal abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor);

  public virtual void MineBlock(int height, Block block, out TXOutputTokenAnchor anchorToken)
  { throw new NotSupportedException(); }

  internal virtual bool TryGetDB(byte[] hash, out byte[] dataDB)
  { throw new NotSupportedException(); }
}
