using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.Helpers
{
    public class ShaderOptionCreator
    {
        public static void SetupOptionKeyFlags(ShaderModel shaderModel)
        {
            List<int> bitfield = new List<int>();
            SetupOptionKeyFlags(bitfield, shaderModel.StaticOptions.Values.ToList());
            //total number of bit keys used
            shaderModel.StaticKeyLength = (byte)bitfield.Count;

            SetupOptionKeyFlags(bitfield, shaderModel.DynamicOptions.Values.ToList());
            //total number of bit keys used
            shaderModel.DynamicKeyLength = (byte)(bitfield.Count - shaderModel.StaticKeyLength);
            //don't apply key table, as those should only be used by added program choices
        }

        private static void SetupOptionKeyFlags(List<int> bitfield, List<ShaderOption> options)
        {
            byte key_offset = (byte)bitfield.Count;

            bitfield.Add(0);

            int bitPos = 32;
            foreach (var option in options)
            {
                // Calculate the number of bits needed for each macro
                int bitsPerChoice = (int)Math.Max(Math.Ceiling(Math.Log2(option.Choices.Count)), 1);
                //shift to store the choices within the bit pos
                bitPos -= bitsPerChoice;

                //Bitpos too low, reset
                if (bitPos < 0)
                {
                    //start pos to new bit uint32
                    bitPos = 32 - bitsPerChoice;
                    //add new key bitfield
                    bitfield.Add(0);
                }

                // Calculate the bit mask for the choices
                //total bits to take up, shift by bit pos
                uint bitMask = (uint)((1 << bitsPerChoice) - 1) ;
                bitMask <<= bitPos;

                uint bitKeyIndex = (uint)bitfield.Count - 1;

               // Console.WriteLine($"{bitKeyIndex} {bitPos} {bitMask} og {option.Bit32Index} {option.Bit32Shift} {option.Bit32Mask} bitsPerChoice {bitsPerChoice} {option.Choices.Count}");

                option.Bit32Mask = (uint)bitMask;
                option.Bit32Shift = (byte)bitPos;
                option.Bit32Index = (byte)bitKeyIndex;
                option.KeyOffset = key_offset;
            }
        }
    }
}
