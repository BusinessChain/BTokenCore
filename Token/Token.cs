using System.Security.Cryptography;


namespace BTokenCore;

public abstract partial class Token
{
  internal const byte LENGTH_SCRIPT_P2PKH = 25;
  internal const int LENGTH_HASH160 = 20;
  internal static byte[] PREFIX_P2PKH = [0x76, 0xA9, 0x14];
  internal static byte[] POSTFIX_P2PKH = [0x88, 0xAC];

  internal const int LENGTH_ID_TOKEN = 4;
  internal Wallet Wallet;

  internal int SizeBlockMax;


  protected Token()
  {
    Directory.CreateDirectory(GetName());

    Wallet = new Wallet(File.ReadAllText($"Wallet{GetName()}/wallet"));
  }

  public int GetSizeBlockBuffer()
  {
    return SizeBlockMax;
  }

  public abstract Header CreateHeaderGenesis();

  internal abstract bool TryGetTX(byte[] hash, out TX tX);

  public abstract void InsertBlock(Block block);

  public virtual void RollBack(Block block) { }

  public abstract Header ParseHeader(byte[] buffer, ref int index, SHA256 sHA256);

  public abstract TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase = false);

  internal string GetName()
  {
    return GetType().Name;
  }

  internal abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor);
}
