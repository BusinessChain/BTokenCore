using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Network
  {
    class InvMessage : MessageNetwork
    {
      public List<Inventory> Inventories = new();


      public InvMessage(List<Inventory> inventories)
        : base("inv")
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
        : base(
            "inv",
            buffer)
      {
        int startIndex = 0;

        int inventoryCount = VarInt.GetInt32(
          Payload,
          ref startIndex);

        for (int i = 0; i < inventoryCount; i++)
          Inventories.Add(Inventory.Parse(
            Payload,
            ref startIndex));
      }
    }
  }
}
