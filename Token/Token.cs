using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenCore;

public abstract partial class Token
{
  public const byte LENGTH_SCRIPT_P2PKH = 25;
  public static byte[] PREFIX_P2PKH = [0x76, 0xA9, 0x14];
  public static byte[] POSTFIX_P2PKH = [0x88, 0xAC];

  public byte[] IDToken;
  public Network Network;
  public Wallet Wallet;

  public int SizeBlockMax;

  public StreamWriter LogFile;

  protected IEnvironment Environment;

  bool IsLocked;


  public int Port;
  public UInt32 ProtocolVersion = 70015;
  public ulong NetworkServicesLocal = 0;
  public ulong NetworkServicesRemote = 0;
  public string UserAgent = "/BTokenCore:0.0.0/";
  public byte RelayOption = 0x01;


  protected Token(IEnvironment environment)
  {
    Directory.CreateDirectory(GetName());

    Wallet = new Wallet(File.ReadAllText($"Wallet{GetName()}/wallet"));

    LogFile = new StreamWriter(Path.Combine(GetName(), "LogToken"), append: false);

    Environment = environment;
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

  internal ISocketCommunication GetSocketCommunication(string address)
  {
    return Environment.GetSocketCommunication(this, address);
  }

  internal void StartListenerCommunicationInbound()
  {
    Environment.StartListenerCommunicationInbound(Port);
  }

  internal async Task<ISocketCommunication> AcceptSocketCommunicationInbound()
  {
    return await Environment.AcceptSocketCommunicationInbound();
  }

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

  internal abstract Header CreateHeaderGenesis();

  internal abstract bool TryGetTX(byte[] hash, out TX tX);

  internal abstract void InsertBlock(Block block);

  internal virtual void ReverseBlock(Block block) { }

  internal abstract Header ParseHeader(byte[] buffer, ref int index, SHA256 sHA256);

  internal abstract TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase = false);

  internal string GetName()
  {
    return GetType().Name;
  }

  internal abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor);

  internal virtual Block MineBlock(int height, out TXOutputTokenAnchor anchorToken)
  { throw new NotSupportedException(); }

  internal virtual bool TryGetDB(byte[] hash, out byte[] dataDB)
  { throw new NotSupportedException(); }
}
