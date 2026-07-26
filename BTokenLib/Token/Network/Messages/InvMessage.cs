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
      partial class Peer
      {
        class InvMessage : MessageNetworkProtocol
        {
          public const string Command = "inv";

          public List<Inventory> Inventories = new();

          public InvMessage()
          { }

          public InvMessage(List<Inventory> inventories)
          {
            Inventories = inventories;

            List<byte> payload = new();

            payload.AddRange(VarInt.GetBytes(inventories.Count));

            Inventories.ForEach(
              i => payload.AddRange(i.GetBytes()));

            Payload = payload.ToArray();
            LengthDataPayload = Payload.Length;
          }

          public InvMessage(byte[] buffer)
            : base(buffer)
          {
            int startIndex = 0;

            int inventoryCount = VarInt.GetInt(
              Payload,
              ref startIndex);

            for (int i = 0; i < inventoryCount; i++)
              Inventories.Add(Inventory.Parse(
                Payload,
                ref startIndex));
          }

          public override async Task Run(Peer peer)
          {

          }

          public override string GetCommand()
          {
            return Command;
          }
        }
      }
    }
  }
}
