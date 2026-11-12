using System.Security.Cryptography;

namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class SynchronizerAccounts
  {
    BlockchainBToken Blockchain;

    Header HeaderAccountsReplayed;

    (byte[] segment, Header header)[] SweepsDownload = new (byte[], Header)[COUNT_SEGMENTS];
    Dictionary<byte[], (Header header, DateTime timeRequestLast)> HeadersSweepAwaited = new(new EqualityComparerByteArray());


    internal SynchronizerAccounts(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
    }

    internal void Load()
    {
      if (Blockchain.Accounts.Exists())
        if (Blockchain.ChainRoot.TryGetHeader(Blockchain.Accounts.GetHashBlockAppliedLast(), out Header headerAccounts))
          HeaderAccountsReplayed = headerAccounts;
        else
          Drop();
    }

    internal void Start()
    {
      if (!Blockchain.Accounts.Exists() && Blockchain.ChainRoot.HeaderRoot.Height == 0)
        InstallAccounts(Blockchain.ChainRoot.HeaderRoot, []);

      ReplayAccounts();
    }

    internal void Drop()
    {
      Blockchain.Accounts.Drop();
      Blockchain.TXPool.Clear();
      HeaderAccountsReplayed = null;
    }

    internal void HandleTipUpdated()
    {
      ReplayAccounts();
      TryInstallSweeps();
    }

    internal void RollBack(Header header)
    {
      if (!Blockchain.Accounts.Exists() || HeaderAccountsReplayed != null && header.Height > HeaderAccountsReplayed.Height)
        return;

      if (header.Height <= Blockchain.Accounts.GetHeightInstalled())
      {
        HeaderAccountsReplayed ??= header;
        return;
      }

      Blockchain.Accounts.RollBack(header);

      if (HeaderAccountsReplayed != null)
        HeaderAccountsReplayed = header.HeaderPrevious;
    }

    internal bool IsAccountsAtTip()
    {
      return Blockchain.Accounts.Exists() && HeaderAccountsReplayed == null;
    }

    internal byte[] GetSegmentSweptByBlock(byte[] hashBlock)
    {
      if (!IsAccountsAtTip() || !Blockchain.ChainRoot.TryGetHeader(hashBlock, out Header header))
        return null;

      return Blockchain.Accounts.SerializeSegmentAtHeight(header.Height);
    }

    internal Header FetchHeaderSweepMissingNext(Func<Header, bool> canSupplierDeliver)
    {
      if (Blockchain.Accounts.Exists() || !IsWindowStoredForAccountsExchange())
        return null;

      return FetchHeaderMissingNext(
        EnumerateHeadersSweepMissing(),
        HeadersSweepAwaited,
        canSupplierDeliver);
    }

    bool IsWindowStoredForAccountsExchange()
    {
      return Blockchain.GetHeightBlockStoredOldest() <= Blockchain.ChainRoot.HeaderTipBlockchain.Height - COUNT_MAX_BLOCKS_STORED + 1;
    }

    IEnumerable<Header> EnumerateHeadersSweepMissing()
    {
      Header header = Blockchain.ChainRoot.HeaderTipBlockchain.GetHeaderAncestor(Blockchain.ChainRoot.HeaderTipBlockchain.Height - BITCOIN_REORG_MARGIN);

      for (int i = 0; i < COUNT_SEGMENTS; i += 1, header = header.HeaderPrevious)
        if (SweepsDownload[header.Height % COUNT_SEGMENTS].header != header)
          yield return header;
    }

    internal bool AddSegmentDownload(byte[] hashBlock, byte[] buffer, int length)
    {
      if (Blockchain.Accounts.Exists()
        || !Blockchain.ChainRoot.TryGetHeader(hashBlock, out Header header)
        || header.Height > Blockchain.ChainRoot.HeaderTipBlockchain.Height)
        return true;

      byte[] segment = buffer[..length];

      if (!SHA256.HashData(segment).IsAllBytesEqual(((HeaderBToken)header).HashAccounts))
        return false;

      int indexSegment = header.Height % COUNT_SEGMENTS;

      if (!DatabaseAccounts.IsSegmentValid(segment, indexSegment))
      {
        Blockchain.RejectBlock(header, new ProtocolException($"Segment {indexSegment} committed by block {header.Hash.ToHexString()} is invalid."));
        Blockchain.UpdateTipBlockchain(Blockchain.ChainRoot);
        return true;
      }

      SweepsDownload[indexSegment] = (segment, header);

      TryInstallSweeps();

      return true;
    }

    void TryInstallSweeps()
    {
      if (Blockchain.Accounts.Exists() || !IsWindowStoredForAccountsExchange() || EnumerateHeadersSweepMissing().Any())
        return;

      Header headerTip = Blockchain.ChainRoot.HeaderTipBlockchain;

      InstallAccounts(headerTip.GetHeaderAncestor(headerTip.Height - BITCOIN_REORG_MARGIN), SweepsDownload);
    }

    void InstallAccounts(Header headerAccountsInstalled, IList<(byte[] segment, Header header)> sweeps)
    {
      Header headerFailed = null;

      try
      {
        Blockchain.LiteDatabase.RunTransaction(() => Blockchain.Accounts.Install(
          sweeps,
          headerAccountsInstalled,
          Blockchain.LoadBlockParsed,
          out headerFailed));
      }
      catch (ProtocolException exception) when (headerFailed != null)
      {
        Drop();
        Blockchain.RejectBlock(headerFailed, exception);
        Blockchain.UpdateTipBlockchain(Blockchain.ChainRoot);
        return;
      }
      catch
      {
        Drop();
        throw;
      }

      Array.Clear(SweepsDownload);
      HeadersSweepAwaited.Clear();

      HeaderAccountsReplayed = headerAccountsInstalled;

      ReplayAccounts();
    }

    void ReplayAccounts()
    {
      if (HeaderAccountsReplayed == null)
        return;

      Header headerTip = Blockchain.ChainRoot.HeaderTipBlockchain;

      try
      {
        if (headerTip.GetHeaderAncestor(HeaderAccountsReplayed.Height) != HeaderAccountsReplayed)
          throw new ProtocolException($"A rollback reached the replayed block {HeaderAccountsReplayed.Hash.ToHexString()}.");

        while (HeaderAccountsReplayed != headerTip)
        {
          Header header = HeaderAccountsReplayed.HeaderNext;
          Block block = Blockchain.LoadBlockParsed(header);

          try
          {
            Blockchain.LiteDatabase.RunTransaction(() => Blockchain.Accounts.InsertBlock(block));
          }
          catch (ProtocolException exception)
          {
            Blockchain.RejectBlock(header, exception);
            break;
          }

          HeaderAccountsReplayed = header;
        }
      }
      catch (Exception exception)
      {
        Console.WriteLine($"Full check of the account database failed, the database is downloaded again: {exception.Message}");

        Drop();
        return;
      }

      HeaderAccountsReplayed = null;

      Blockchain.UpdateTipBlockchain(Blockchain.ChainRoot);

      Blockchain.LoadTXsOwnedUnconfirmed();
    }
  }
}
