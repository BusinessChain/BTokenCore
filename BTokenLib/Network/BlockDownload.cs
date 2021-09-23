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
      public BlockDownload BlockDownloadIndexPrevious;
      public Peer Peer;

      public List<Header> HeadersExpected = new();
      public int IndexHeadersExpected;
      public List<Block> Blocks = new();

      public bool IsComplete;

      public Stopwatch StopwatchBlockDownload = new();

      const int COUNT_BLOCK_MAX = 10;



      public BlockDownload(Token token)
      {
        for (int i = 0; i < COUNT_BLOCK_MAX; i += 1)
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
        IndexHeadersExpected = 0;
        IsComplete = false;

        do
        {
          HeadersExpected.Add(headerLoad);
          headerLoad = headerLoad.HeaderNext;
        } while (
        HeadersExpected.Count < countHeadersNew
        && headerLoad != null);
      }

      public byte[] GetBufferToParse()
      {
        return Blocks[IndexHeadersExpected].Buffer;
      }

      public void Parse()
      {
        Block block = Blocks[IndexHeadersExpected];

        block.Parse();

        if (!block.Header.Hash.IsEqual(
          HeadersExpected[IndexHeadersExpected].Hash))
        {
          throw new ProtocolException(string.Format(
            "Unexpected block header {0} in blockLoad {1}. \n" +
            "Excpected {2}.",
            block.Header.Hash.ToHexString(),
            Index,
            HeadersExpected[IndexHeadersExpected].Hash.ToHexString()));
        }

        IndexHeadersExpected += 1;

        if (IndexHeadersExpected == HeadersExpected.Count)
        {
          IsComplete = true;
          StopwatchBlockDownload.Stop();
        }
      }

      public List<Block> GetBlocks()
      {
        return Blocks.Take(HeadersExpected.Count).ToList();
      }
    }
  }
}
