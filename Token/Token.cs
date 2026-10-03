using System.Text;
using System.Security.Cryptography;


namespace BTokenCore;

public abstract partial class Token : IToken
{
  internal const byte LENGTH_SCRIPT_P2PKH = 25;
  internal static byte[] PREFIX_P2PKH = [0x76, 0xA9, 0x14];
  internal static byte[] POSTFIX_P2PKH = [0x88, 0xAC];

  internal const int LENGTH_ID_TOKEN = 4;
  internal byte[] IDToken = new byte [LENGTH_ID_TOKEN];
  internal Wallet Wallet;

  internal int SizeBlockMax;


  protected Token(string id)
  {
    byte[] bytes = Encoding.ASCII.GetBytes(id);
    Array.Copy(bytes, IDToken, bytes.Length);

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

  public virtual Block MineBlock(Header headerPrevious, Block block, out TXOutputTokenAnchor anchorToken)
  { throw new NotSupportedException(); }

  internal virtual bool TryGetDB(byte[] hash, out byte[] dataDB)
  { throw new NotSupportedException(); }
}
