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

      public Stopwatch StopwatchBlockDownload = new();

      public long TimeBlockDownloadCompletion;

      public int CountBytes;

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
        CountBytes = 0;

        do
        {
          HeadersExpected.Add(headerLoad);
          headerLoad = headerLoad.HeaderNext;
        } while (
        HeadersExpected.Count < countHeadersNew
        && headerLoad != null);
      }

      public Block GetBlockToParse()
      {
        if(IsComplete())
        {
          IndexHeadersExpected = 0;
        }

        return Blocks[IndexHeadersExpected];
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
        CountBytes += block.IndexBufferStop;

        if (IsComplete())
        {
          TimeBlockDownloadCompletion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
          StopwatchBlockDownload.Stop();
        }
      }

      public bool IsComplete() => 
        IndexHeadersExpected == HeadersExpected.Count;
    }
  }
}
