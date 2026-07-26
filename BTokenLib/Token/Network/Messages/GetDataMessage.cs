using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      class GetDataMessage : MessageNetworkProtocol
      {
        public const string Command = "getdata";

        Block BlockUpload;


        int HeightBlockDownloadedLast;


        public GetDataMessage(Block blockUpload)
          : base()
        {
          BlockUpload = blockUpload;

          DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5);
        }

        public override async Task Run(Peer peer)
        {
          int startIndex = 0;

          int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

          for (int i = 0; i < inventoryCount; i++)
          {
            Inventory inventory = Inventory.Parse(Payload, ref startIndex);

            if (inventory.Type == InventoryType.MSG_TX)
            {
              if (peer.Network.Token.TryGetTX(inventory.Hash, out TX tXInPool))
                TXMessage.Send(peer, tXInPool.TXRaw);
            }
            else if (inventory.Type == InventoryType.MSG_BLOCK)
            {
              BlockUpload.Header = null;

              await peer.Network.GetBlock(inventory.Hash, BlockUpload);

              if (BlockUpload.Header != null)
              {
                BlockMessage.SendBlock(peer, BlockUpload);

                if (BlockUpload.Header.Height > HeightBlockDownloadedLast)
                  DOSMonitor.Decrement(1);

                HeightBlockDownloadedLast = BlockUpload.Header.Height;
              }
            }
            else if (inventory.Type == InventoryType.MSG_DB)
            {
            }
          }
        }

        public static async Task SendBlockRequest(Peer peer, byte[] hash)
        {
          List<byte> payload = new();

          payload.AddRange(VarInt.GetBytes(1));
          payload.AddRange(BitConverter.GetBytes((uint)InventoryType.MSG_BLOCK));
          payload.AddRange(hash);

          byte[] buffer = payload.ToArray();

          await peer.SendMessage(Command, buffer.Length, buffer);
        }

        public override string GetCommand()
        {
          return Command;
        }
      }
    }
  }
}
