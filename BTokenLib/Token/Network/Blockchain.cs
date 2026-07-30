using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;


namespace BTokenCore;

public abstract partial class Token
{
  partial class NetworkToken
  {
    class Blockchain
    {
      Blockchain BlockchainParent;
      List<Blockchain> BlockchainBranches = new();

      Token Token;

      NetworkToken Network;

      Header HeaderTip;
      Header HeaderRoot;
      Header HeaderTipBlockchain;

      string PathDirectoryBlocks;
      DirectoryInfo DirectoryBlocks;

      Dictionary<byte[], Header> HeadersDownloading = new(new EqualityComparerByteArray());
      Header HeaderDownloadNext;

      const int CAPACITY_MAX_QueueBlocksInsertion = 20;
      Dictionary<int, Block> QueueBlocks = new();

      Block BlockLoad;


      public Blockchain(Token token, NetworkToken network)
      {
        Token = token;

        Network = network;

        BlockLoad = new(Token);

        DirectoryBlocks = Directory.CreateDirectory("blocksRoot");
      }

      public Blockchain(Blockchain blockchainParent, Header headerRoot, Header headerTip)
      {
        BlockchainParent = blockchainParent;
        HeaderRoot = headerRoot;
        HeaderTip = headerTip;

        string pathDirectory = Path.Combine(
          blockchainParent.DirectoryBlocks.FullName,
          "branch" + blockchainParent.BlockchainBranches.Count.ToString());

        DirectoryBlocks = Directory.CreateDirectory(pathDirectory);
      }


      public void LoadFromDisk()
      {
        int heightBlockNext = DirectoryBlocks.GetFiles()
        .Select(file => Path.GetFileNameWithoutExtension(file.Name))
        .Where(name => int.TryParse(name, out _))
        .Select(name => int.Parse(name))
        .DefaultIfEmpty(0)
        .Min();

        while (true)
          try
          {
            BlockLoad.Header = null;
            LoadBlock(heightBlockNext, BlockLoad);

            Token.InsertBlock(BlockLoad);

            if (HeaderRoot == null)
              HeaderRoot = BlockLoad.Header;
            else
            {
              BlockLoad.Header.AppendToHeader(HeaderTip);
              HeaderTip.HeaderNext = BlockLoad.Header;
            }

            HeaderTip = BlockLoad.Header;

            heightBlockNext += 1;
          }
          catch (Exception ex)
          {
            break;
          }

        if (HeaderRoot == null)
        {
          HeaderRoot = Token.CreateHeaderGenesis();
          HeaderTip = HeaderRoot;
        }
      }

      public int GetHeight()
      {
        return HeaderTip.Height;
      }

      public bool IsHigherThan(Blockchain sync)
      {
        return HeaderTip.Height > sync.HeaderTip.Height;
      }

      public bool TryExtendHeaderchain(
        Header header,
        out List<byte[]> locator,
        Block blockDownload)
      {
        locator = null;

        if (header == null)
          return false;

        Header headerAncestor = HeaderTip;

        while (!headerAncestor.Hash.IsAllBytesEqual(header.HashPrevious))
        {
          if (headerAncestor == HeaderRoot)
          {
            foreach (Blockchain sync in BlockchainBranches)
              if (sync.TryExtendHeaderchain(header, out locator, blockDownload))
                return true;

            locator = GetLocator();
            return false;
          }

          headerAncestor = headerAncestor.HeaderPrevious;
        }

        while (headerAncestor != HeaderTip)
        {
          if (headerAncestor.HeaderNext.Hash.IsAllBytesEqual(header.Hash) == false)
          {
            foreach (Blockchain sync in BlockchainBranches)
              if (sync.HeaderRoot.Hash.IsAllBytesEqual(header.Hash))
                return sync.TryExtendHeaderchain(header.HeaderNext, out locator, blockDownload);

            Header headerTip = header.AppendToHeader(headerAncestor);
            Blockchain syncBranch = new(this, header, headerTip);
            BlockchainBranches.Add(syncBranch);

            blockDownload.Header = syncBranch.FetchHeaderDownload();
            locator = new List<byte[]> { headerTip.Hash };
            return false;
          }

          if (header.HeaderNext == null)
          {
            blockDownload.Header = FetchHeaderDownload();
            locator = null;
            return false;
          }

          headerAncestor = headerAncestor.HeaderNext;
          header = header.HeaderNext;
        }

        blockDownload.Header = FetchHeaderDownload();

        Header headerTipNew = header.AppendToHeader(HeaderTip);
        HeaderTip.HeaderNext = header;
        HeaderTip = headerTipNew;

        locator = new List<byte[]> { HeaderTip.Hash };
        return true;
      }

      Header FetchHeaderDownload()
      {
        if ((QueueBlocks.Count > CAPACITY_MAX_QueueBlocksInsertion || HeaderDownloadNext == null)
            && HeadersDownloading.Any())
          return HeadersDownloading.Values.MinBy(h => h.Height);

        if (HeaderDownloadNext != null)
        {
          Header headerDownload = HeaderDownloadNext;
          HeadersDownloading.Add(headerDownload.Hash, headerDownload);
          HeaderDownloadNext = HeaderDownloadNext.HeaderNext;
          return headerDownload;
        }

        return null;
      }

      public Block MineBlock(out TXOutputTokenAnchor anchorToken)
      {
        int height = HeaderTip.Height + 1;

        Block block = Token.MineBlock(height, out anchorToken);

        block.Header.HashPrevious = HeaderTip.Hash;

        block.Header.ComputeHash();

        block.Serialize();

        return block;
      }

      public bool TryAppendBlock(ref Block block, ref Blockchain blockchainRoot)
      {
        if (!HeadersDownloading.Remove(block.Header.Hash))
        {
          foreach (Blockchain branch in BlockchainBranches)
            if (branch.TryAppendBlock(ref block, ref blockchainRoot))
              return true;

          if (IsRoot())
            block.Header = null;

          return false;
        }

        int heightBlock = block.Header.Height;

        if (heightBlock == HeaderTipBlockchain.Height + 1)
        {
          do
          {
            if (IsRoot())
            {
              Token.InsertBlock(block);
              Network.NotifyChildNetworksOfAnchorToken(block);
            }

            HeaderTipBlockchain = block.Header;

            block.WriteToDisk(PathDirectoryBlocks);

            Network.PoolBlocks.Add(block);

          } while (QueueBlocks.TryGetValue(HeaderTipBlockchain.Height + 1, out block));

          if (TryReorg())
            blockchainRoot = this;
        }
        else
          QueueBlocks.Add(heightBlock, block);

        if (!Network.PoolBlocks.TryTake(out block))
          block = new(Token);

        block.Header = FetchHeaderDownload();

        return true;
      }

      bool TryReorg()
      {
        if (IsRoot() || !IsStrongerThan(BlockchainParent))
          return false;

        Header headerAncestor = HeaderRoot.HeaderPrevious;

        if (BlockchainParent.IsRoot() && !TryReorgToken(headerAncestor.Height))
          return false;

        SwitchWithParentBranch(headerAncestor);

        if (!IsRoot())
          return TryReorg();

        return true;
      }

      bool TryReorgToken(int heightAncestor)
      {
        BlockchainParent.RewindTokenToHeight(heightAncestor);

        Token = BlockchainParent.Token;

        try
        {
          RollTokenForwardToTip(heightAncestor);
        }
        catch
        {
          Token = null;

          BlockchainParent.RollTokenForwardToTip(heightAncestor);

          return false;
        }

        BlockchainParent.Token = null;

        return true;
      }

      void RewindTokenToHeight(int heightAncestor)
      {
        int height = HeaderTip.Height;

        while (height > heightAncestor)
        {
          BlockLoad.Header = null;
          LoadBlock(height, BlockLoad);

          Token.ReverseBlock(BlockLoad);

          height--;
        }
      }

      void RollTokenForwardToTip(int heightAncestor)
      {
        int height = heightAncestor + 1;

        while (height <= HeaderTip.Height)
        {
          BlockLoad.Header = null;
          LoadBlock(height, BlockLoad);

          Token.InsertBlock(BlockLoad);

          height++;
        }
      }

      void SwitchWithParentBranch(Header headerAncestor)
      {
        Header headerRootNewSyncParent = headerAncestor.HeaderNext;
        headerAncestor.HeaderNext = HeaderRoot;
        HeaderRoot = BlockchainParent.HeaderRoot;
        BlockchainParent.HeaderRoot = headerRootNewSyncParent;

        List<Blockchain> branches = BlockchainParent.BlockchainBranches.ToList();

        foreach (Blockchain syncBranch in branches)
          if (syncBranch.HeaderRoot.Height <= HeaderRoot.Height)
          {
            BlockchainParent.BlockchainBranches.Remove(syncBranch);

            if (syncBranch != this)
            {
              syncBranch.BlockchainParent = this;
              BlockchainBranches.Add(syncBranch);
            }
          }

        BlockchainBranches.Add(BlockchainParent);

        Blockchain syncParentNew = BlockchainParent.BlockchainParent;
        BlockchainParent.BlockchainParent = this;
        BlockchainParent = syncParentNew;
      }

      bool IsRoot()
      {
        return BlockchainParent == null;
      }

      bool IsStrongerThan(Blockchain blockchain)
      {
        return HeaderTipBlockchain.DifficultyAccumulated > blockchain.HeaderTipBlockchain.DifficultyAccumulated;
      }

      public void GetBlock(byte[] hash, Block blockUpload)
      {
        Header header = HeaderRoot;

        while (header != null)
        {
          if (header.Hash.IsAllBytesEqual(hash))
          {
            blockUpload.Header = header;
            LoadBlock(header.Height, blockUpload);
            return;
          }

          header = header.HeaderNext;
        }

        foreach (Blockchain syncBranch in BlockchainBranches)
        {
          syncBranch.GetBlock(hash, blockUpload);

          if (blockUpload.Header != null)
            return;
        }
      }

      public void LoadBlock(int height, Block blockUpload)
      {
        string pathFile = Path.Combine(DirectoryBlocks.FullName, height.ToString());

        using FileStream fileBlock = File.OpenRead(pathFile);

        if (fileBlock.Length > blockUpload.Buffer.Length)
          throw new InvalidOperationException("Block too large for buffer.");

        blockUpload.LengthDataPayload = (int)fileBlock.Length;

        int offset = 0;
        while (offset < blockUpload.LengthDataPayload)
        {
          int n = fileBlock.Read(
              blockUpload.Buffer,
              offset,
              blockUpload.LengthDataPayload - offset);

          if (n == 0)
            throw new EndOfStreamException();

          offset += n;
        }

        blockUpload.Parse();
      }

      public List<byte[]> GetLocator()
      {
        Header header = HeaderTip;
        List<byte[]> locator = new();
        int depth = 0;
        int nextLocationDepth = 0;

        while (header != null)
        {
          if (depth == nextLocationDepth || header.HeaderPrevious == null)
          {
            locator.Add(header.Hash);
            nextLocationDepth = 2 * nextLocationDepth + 1;
          }

          depth++;
          header = header.HeaderPrevious;
        }

        return locator;
      }

      public (List<byte[]> headers, int heightAncestor) GetHeadersSerialized(
        List<byte[]> hashesLocator,
        int maxCountHeaders)
      {
        Header header = HeaderTip;

        while (header != null)
        {
          foreach (byte[] hashLocator in hashesLocator)
            if (header.Hash.IsAllBytesEqual(hashLocator))
              goto LABEL_HeaderAncestorFound;

          header = header.HeaderPrevious;
        }

        return (headers: new(), heightAncestor: -1);

      LABEL_HeaderAncestorFound:

        List<byte[]> headers = new();
        int heightAncestor = header.Height;

        while (header.HeaderNext != null && headers.Count < maxCountHeaders)
        {
          headers.Add(header.HeaderNext.Serialize());
          header = header.HeaderNext;
        }

        return (headers, heightAncestor);
      }
    }
  }
}
