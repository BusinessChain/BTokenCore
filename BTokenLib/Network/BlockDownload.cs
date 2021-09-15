using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;


namespace BTokenLib
{
  partial class Network
  {
    class BlockDownload
    {
      public int Index;
      public Peer Peer;

      public List<Header> HeadersExpected = new();
      List<Block> Blocks = new();
      public int IndexHeaderExpected;
      public int IndexNextBlockToParse;

      public Stopwatch StopwatchBlockDownload = new();

      const int COUNT_BLOCK_MAX = 10;



      public BlockDownload(
        Token token, 
        int index, 
        Peer peer)
      {
        Index = index;
        Peer = peer;

        for(int i = 0; i< COUNT_BLOCK_MAX; i += 1)
        {
          Block block = token.CreateBlock(0x400000);
          Blocks.Add(block);
        }
      }

      public void LoadHeaders(ref Header headerLoad)
      {
        int countHeadersNew = HeadersExpected.Count * 
          (int)(TIMEOUT_RESPONSE_MILLISECONDS /
          (double)StopwatchBlockDownload.ElapsedMilliseconds);

        countHeadersNew = countHeadersNew > COUNT_BLOCK_MAX ? 
          COUNT_BLOCK_MAX : countHeadersNew;

        HeadersExpected.Clear();

        do
        {
          HeadersExpected.Add(headerLoad);
          headerLoad = headerLoad.HeaderNext;
        } while (
        HeadersExpected.Count < countHeadersNew
        && headerLoad != null);
      }

      public Block GetNextBlockToParse()
      {
        return Blocks[IndexNextBlockToParse];
      }

      public bool InsertBlockFlagComplete(Block block)
      {
        if (!block.Header.Hash.IsEqual(
          HeadersExpected[IndexHeaderExpected].Hash))
        {
          throw new ProtocolException(string.Format(
            "Unexpected block header {0} in blockLoad {1}. \n" +
            "Excpected {2}.",
            block.Header.Hash.ToHexString(),
            Index,
            HeadersExpected[IndexHeaderExpected].Hash.ToHexString()));
        }

        Blocks.Add(block);

        IndexHeaderExpected += 1;

        if(IndexHeaderExpected == HeadersExpected.Count)
        {
          StopwatchBlockDownload.Stop();
          return true;
        }

        return false;
      }

      public List<Block> GetBlocks()
      {
        return Blocks.Take(HeadersExpected.Count).ToList();
      }
    }
  }
}
