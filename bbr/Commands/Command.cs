﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public abstract class Command
    {
        public Command()
        {

        }

        public abstract int CommandId { get; }
        public static ulong SentPacketCount { get; set; }
        public ulong PacketNumber { get; set; }

        protected abstract void Deserialize(BinaryReader reader);
        protected abstract void Serialize(BinaryWriter writer);

        public void Serialise(BinaryWriter writer)
        {
            writer.Write(CommandId);

            PacketNumber = SentPacketCount++;
            writer.Write(PacketNumber);

            Serialize(writer);
        }

        public static Command? Deserialise(BinaryReader reader)
        {
            var commandId = reader.ReadInt32();

            Command? result = commandId switch
            {
                Connect.COMMAND_ID => new Connect(),
                Forward.COMMAND_ID => new Forward(),
                Purge.COMMAND_ID => new Purge(),
                TearDown.COMMAND_ID => new TearDown(),
                _ => null
            };

            if (result != null)
            {
                result.PacketNumber = reader.ReadUInt64();
                result.Deserialize(reader);
            }

            return result;
        }
    }
}
