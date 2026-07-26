using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public const byte LENGTH_SCRIPT_P2PKH = 25;
    public static byte[] PREFIX_P2PKH = new byte[] { 0x76, 0xA9, 0x14 };
    public static byte[] POSTFIX_P2PKH = new byte[] { 0x88, 0xAC };

    public byte[] IDToken;
    protected NetworkToken Network;
    public Wallet Wallet;

    public int SizeBlockMax;

    public StreamWriter LogFile;
    public ILogEntryNotifier LogEntryNotifier;

    bool IsLocked;


    public Token(ILogEntryNotifier logEntryNotifier)
    {
      Directory.CreateDirectory(GetName());

      Wallet = new Wallet(File.ReadAllText($"Wallet/wallet"));

      LogFile = new StreamWriter(Path.Combine(GetName(), "LogToken"), append: false);

      LogEntryNotifier = logEntryNotifier;
    }

    public abstract List<string> GetSeedAddresses();

    public bool TryLock()
    {
      lock (this)
      {
        if (IsLocked)
          return false;

        IsLocked = true;
        return true;
      }
    }

    public void ReleaseLock()
    {
        IsLocked = false;
    }

    public abstract Header CreateHeaderGenesis();

    public abstract bool TryGetTX(byte[] hash, out TX tX);

    public abstract void InsertBlock(Block block);

    public virtual void ReverseBlock(Block block) { }

    public abstract Header ParseHeader(byte[] buffer, ref int index, SHA256 sHA256);

    public abstract TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase = false);

    public string GetName()
    {
      return GetType().Name;
    }

    public abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor);

    public virtual Block MineBlock(int height, out TXOutputTokenAnchor anchorToken)
    { throw new NotSupportedException(); }

    public virtual bool TryGetDB(byte[] hash, out byte[] dataDB)
    { throw new NotSupportedException(); }
  }
}
