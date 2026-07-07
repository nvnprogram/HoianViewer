using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShaderLibrary.Helpers
{
    public class ShaderOptionSearcher
    {
        //Program key table indexed by hashed key vector, built once per shader model.
        //Without this every lookup scans all programs, and the lenient fallback scan
        //does string work per program per option, which takes seconds per material.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderModel, Dictionary<KeyVector, int>> _programLookups = new();

        readonly struct KeyVector : IEquatable<KeyVector>
        {
            readonly int[] _keys;
            readonly int _offset;
            readonly int _length;
            readonly int _hash;

            public KeyVector(int[] keys, int offset, int length)
            {
                _keys = keys;
                _offset = offset;
                _length = length;

                int hash = 17;
                for (int i = 0; i < length; i++)
                    hash = hash * 31 + keys[offset + i];
                _hash = hash;
            }

            public bool Equals(KeyVector other)
            {
                if (_length != other._length || _hash != other._hash)
                    return false;
                for (int i = 0; i < _length; i++)
                {
                    if (_keys[_offset + i] != other._keys[other._offset + i])
                        return false;
                }
                return true;
            }

            public override bool Equals(object obj) => obj is KeyVector other && Equals(other);
            public override int GetHashCode() => _hash;
        }

        static Dictionary<KeyVector, int> GetProgramLookup(ShaderModel shader)
        {
            return _programLookups.GetValue(shader, s =>
            {
                int stride = s.StaticKeyLength + s.DynamicKeyLength;
                var lookup = new Dictionary<KeyVector, int>(s.Programs.Count);
                for (int i = 0; i < s.Programs.Count; i++)
                {
                    var key = new KeyVector(s.KeyTable, stride * i, stride);
                    if (!lookup.ContainsKey(key))
                        lookup.Add(key, i);
                }
                return lookup;
            });
        }

        //Profiling: exact hashed hits vs full lenient scans (the expensive path).
        public static readonly Stopwatch SearchTime = new Stopwatch();
        public static int ExactHits, LenientScans;

        public static int GetProgramIndex(ShaderModel shader, Dictionary<string, string> options)
        {
            SearchTime.Start();
            try
            {
                //Generate keys of the shader options and look them up in the hashed key table.
                int[] key_lookup = WriteOptionKeys(shader, options);
                if (GetProgramLookup(shader).TryGetValue(new KeyVector(key_lookup, 0, key_lookup.Length), out int index))
                {
                    ExactHits++;
                    return index;
                }

                //Fall back to a lenient search: options explicitly given by the material are
                //hard constraints, and among matching programs the one whose remaining keys
                //are closest to the option defaults wins. Materials leave many options at
                //<Default Value> that the game engine derives at runtime, so an exact key
                //match is not always possible.
                LenientScans++;
                return LenientSearch(shader, options, key_lookup);
            }
            finally { SearchTime.Stop(); }
        }

        /// <summary>
        /// Scans all programs treating the options explicitly given by the material as
        /// hard constraints, and among the programs that satisfy them returns the one
        /// whose remaining option keys are closest to the expected (default) key vector.
        /// The query is precompiled to (key index, mask, shift, choice index) so the
        /// per-program work is integer-only.
        /// </summary>
        static int LenientSearch(ShaderModel shader, Dictionary<string, string> options, int[] expectedKeys)
        {
            int stride = shader.StaticKeyLength + shader.DynamicKeyLength;

            var checks = new List<(int keyIndex, uint mask, int shift, int choiceIdx)>();
            var scored = new List<(int keyIndex, uint mask, int shift, int expectedIdx)>();

            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                int keyIndex = option.Bit32Index;
                if (options.TryGetValue(option.Name, out string choice))
                {
                    int choiceIndex = option.Choices.GetIndex(choice);
                    if (choiceIndex == -1)
                        return -1; //choice does not exist in this shader model
                    checks.Add((keyIndex, option.Bit32Mask, option.Bit32Shift, choiceIndex));
                }
                else
                {
                    int expectedIdx = (int)(((uint)expectedKeys[keyIndex] & option.Bit32Mask) >> option.Bit32Shift);
                    scored.Add((keyIndex, option.Bit32Mask, option.Bit32Shift, expectedIdx));
                }
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                int keyIndex = shader.StaticKeyLength + option.Bit32Index - option.KeyOffset;
                if (options.TryGetValue(option.Name, out string choice))
                {
                    int choiceIndex = option.Choices.GetIndex(choice);
                    if (choiceIndex == -1)
                        return -1;
                    checks.Add((keyIndex, option.Bit32Mask, option.Bit32Shift, choiceIndex));
                }
                else
                {
                    int expectedIdx = (int)(((uint)expectedKeys[keyIndex] & option.Bit32Mask) >> option.Bit32Shift);
                    scored.Add((keyIndex, option.Bit32Mask, option.Bit32Shift, expectedIdx));
                }
            }

            var table = shader.KeyTable;
            int best = -1, bestScore = -1;
            for (int i = 0; i < shader.Programs.Count; i++)
            {
                int baseIndex = stride * i;
                bool match = true;
                foreach (var (keyIndex, mask, shift, choiceIdx) in checks)
                {
                    if (((table[baseIndex + keyIndex] & mask) >> shift) != choiceIdx)
                    {
                        match = false;
                        break;
                    }
                }
                if (!match)
                    continue;

                int score = 0;
                foreach (var (keyIndex, mask, shift, expectedIdx) in scored)
                {
                    if (((table[baseIndex + keyIndex] & mask) >> shift) == expectedIdx)
                        score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                    if (score == scored.Count)
                        break; //cannot do better
                }
            }
            return best;
        }

        static bool IsMatch(ShaderModel shader, int programIdx, int[] keys)
        {
            int num_keys_per_program = shader.StaticKeyLength + shader.DynamicKeyLength;
            var idx = num_keys_per_program * programIdx;

            //Direct array compare; LINQ Skip/Take here is O(idx) per program which
            //makes the full search quadratic on large key tables.
            var table = shader.KeyTable;
            for (int i = 0; i < keys.Length; i++)
            {
                if (table[idx + i] != keys[i])
                    return false;
            }
            return true;
        }

        public static int[] WriteOptionKeys(ShaderModel shader, Dictionary<string, string> options)
        {
            //Setup default keys
            int[] key_lookup = WriteDefaultKey(shader);

            //Setup static and dynamic keys
            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                //Set the static option choice
                int choiceIndex = option.Choices.GetIndex(options[option.Name]);
                if (choiceIndex == -1)
                    throw new Exception(string.Format("Invalid choice given {1} for option {0}!", option.Name, options[option.Name]));

                option.SetKey(ref key_lookup[option.Bit32Index], choiceIndex);
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                //Set the dynamic option choice
                int choiceIndex = option.Choices.GetIndex(options[option.Name]);
                if (choiceIndex == -1)
                    throw new Exception(string.Format("Invalid choice given {1} for option {0}!", option.Name, options[option.Name]));

                int ind = option.Bit32Index - option.KeyOffset;
                option.SetKey(ref key_lookup[shader.StaticKeyLength + ind], choiceIndex);
            }
            return key_lookup;
        }

        static int[] WriteDefaultKey(ShaderModel shader)
        {
            int num_keys = shader.StaticKeyLength + shader.DynamicKeyLength;

            int[] keys = new int[num_keys];

            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                //Set the default static option choice
                option.SetKey(ref keys[option.Bit32Index], option.DefaultChoiceIdx);
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];

                //Set the default dynamic option choice.
                //Dynamic keys live after the static keys and are relative to KeyOffset;
                //writing to Bit32Index directly would corrupt the static key area.
                int ind = option.Bit32Index - option.KeyOffset;
                option.SetKey(ref keys[shader.StaticKeyLength + ind], option.DefaultChoiceIdx);
            }

            return keys;
        }

        public static bool IsValidProgram(ShaderModel shader, int programIndex, Dictionary<string, string> options)
        {
            //The amount of keys used per program
            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;

            //Static key (total * program index)
            int baseIndex = numKeysPerProgram * programIndex;

            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                //The options must be the same between bfres and bfsha
                if (!options.ContainsKey(option.Name))
                    continue;

                //Get key in table
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex > option.Choices.Count)
                    throw new Exception($"Invalid choice index in key table! Option {option.Name} choice {options[option.Name]}");

                //If the choice is not in the program, then skip the current program
                var choice = option.Choices.GetKey(choiceIndex);
                if (options[option.Name] != choice)
                    return false;
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + shader.StaticKeyLength + ind]);
                if (choiceIndex > option.Choices.Count)
                    throw new Exception($"Invalid choice index in key table!");

                var choice = option.Choices.GetKey(choiceIndex);
                if (options[option.Name] != choice)
                    return false;
            }
            return true;
        }

        //Checks if the shader option list is missing any shader option choices required for a full key search
        public static void CheckMissingShaderOptions(ShaderModel shader, Dictionary<string, string> options)
        {
            int num_keys_per_program = shader.StaticKeyLength + shader.DynamicKeyLength;
            for (int i = 0; i < shader.Programs.Count; i++)
            {
                if (IsValidProgram(shader, i, options))
                    CheckChoices(shader, i, options);
            }
        }

        static void CheckChoices(ShaderModel shader, int programIndex, Dictionary<string, string> options)
        {
            Debug.WriteLine($"checking program {programIndex}");

            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;

            var maxBit = shader.StaticOptions.Values.Max(x => x.Bit32Index);
            int baseIndex = numKeysPerProgram * programIndex;
            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex > option.Choices.Count || choiceIndex == -1)
                    throw new Exception($"Invalid choice index in key table! {option.Name} index {choiceIndex}");

                string choice = option.Choices.GetKey(choiceIndex);

                //A shader option choice not set in the lookup and not a default choice
                //This must be set for a valid lookup
                if (!options.ContainsKey(option.Name) && choice != option.DefaultChoice)
                    Debug.WriteLine($"Unexpected choice value {option.Name} should be {choice}, not default {option.DefaultChoice}");
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + shader.StaticKeyLength + ind]);
                if (choiceIndex > option.Choices.Count || choiceIndex == -1)
                    throw new Exception($"Invalid choice index in key table! {option.Name} index {choiceIndex}");


                string choice = option.Choices.GetKey(choiceIndex);
                if (!options.ContainsKey(option.Name) && choice != option.DefaultChoice)
                    Debug.WriteLine($"Unexpected choice value {option.Name} should be {choice}, not default {option.DefaultChoice}");
            }
        }
    }
}
