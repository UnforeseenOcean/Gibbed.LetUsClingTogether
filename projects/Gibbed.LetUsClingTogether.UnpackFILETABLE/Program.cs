﻿/* Copyright (c) 2020 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gibbed.IO;
using Gibbed.LetUsClingTogether.FileFormats;
using NDesk.Options;
using Newtonsoft.Json;

namespace Gibbed.LetUsClingTogether.UnpackFILETABLE
{
    internal class Program
    {
        private static string GetExecutableName()
        {
            return Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase);
        }

        public static void Main(string[] args)
        {
            bool unpackNestedPacks = true;
            bool verbose = false;
            bool showHelp = false;

            var options = new OptionSet()
            {
                { "d|dont-unpack-nested-packs", "don't unpack nested .pack files", v => unpackNestedPacks = v == null },
                { "v|verbose", "be verbose (list files)", v => verbose = v != null },
                { "h|help", "show this message and exit",  v => showHelp = v != null },
            };

            List<string> extra;
            try
            {
                extra = options.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write("{0}: ", GetExecutableName());
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `{0} --help' for more information.", GetExecutableName());
                return;
            }

            if (extra.Count < 1 || extra.Count > 2 || showHelp == true)
            {
                Console.WriteLine("Usage: {0} [OPTIONS]+ input_FILETABLE [output_directory]", GetExecutableName());
                Console.WriteLine("Unpack specified archive.");
                Console.WriteLine();
                Console.WriteLine("Options:");
                options.WriteOptionDescriptions(Console.Out);
                return;
            }

            string inputPath = extra[0];
            string outputBasePath = extra.Count > 1 ? extra[1] : Path.ChangeExtension(inputPath, null) + "_unpacked";

            FileTableFile table;
            using (var input = File.OpenRead(inputPath))
            {
                table = new FileTableFile();
                table.Deserialize(input);
            }

            var inputBasePath = Path.GetDirectoryName(inputPath);

            // TODO(gibbed):
            //  - generate file index for successful repacking
            //  - better name lookup for name hashes (FNV32)
            //    (don't hardcode the list)
            var names = new string[]
            {
                "MENU_COMMON_PACK",
                "MENU_TEXTURE_MISC_PACK",
                "MN_AT_ORGANIZE",
                "MN_BIRTHDAY",
                "MN_BT_MAIN",
                "MN_BT_RESULT",
                "MN_COMMON",
                "MN_COMMONWIN",
                "MN_EVENT",
                "MN_INPUT",
                "MN_ITEMICON",
                "MN_KEY_LAYOUT",
                "MN_MOVIE",
                "MN_NETWORK",
                "MN_OPTION",
                "MN_ORGANIZE",
                "MN_SHOP2",
                "MN_STAFFROLL",
                "MN_STATUS",
                "MN_TITLE",
                "MN_WARRENREPORT",
                "MN_WORLD",
            };
            var nameHashLookup = names.ToDictionary(v => v.HashFNV32(), v => v);

            var tableManifestPath = Path.Combine(outputBasePath, "@manifest.json");
            var tableManifest = new FileTableManifest()
            {
                TitleId1 = table.TitleId1,
                TitleId2 = table.TitleId2,
                Unknown32 = table.Unknown32,
                ParentalLevel = table.ParentalLevel,
                InstallDataCryptoKey = table.InstallDataCryptoKey,
            };

            foreach (var directory in table.Directories)
            {
                var tableDirectory = new TableDirectory()
                {
                    Id = directory.Id,
                    BasePath = Path.Combine(outputBasePath, $"{directory.Id}"),
                };

                var fileContainers = new List<IFileContainer>()
                {
                    tableDirectory,
                };

                var binPath = Path.Combine(inputBasePath, $"{directory.Id:X4}.BIN");
                using (var input = File.OpenRead(binPath))
                {
                    var fileQueue = new Queue<QueuedFile>();
                    foreach (var file in directory.Files)
                    {
                        long dataOffset;
                        dataOffset = directory.DataBaseOffset;
                        dataOffset += (file.DataBlockOffset << directory.DataBlockSize) * FileTableFile.BaseDataBlockSize;

                        fileQueue.Enqueue(new QueuedFile()
                        {
                            Id = file.Id,
                            Parent = tableDirectory,
                            NameHash = file.NameHash,
                            DataOffset = dataOffset,
                            DataSize = file.DataSize,
                        });
                    }

                    while (fileQueue.Count > 0)
                    {
                        var file = fileQueue.Dequeue();
                        var parent = file.Parent;

                        var nameBuilder = new StringBuilder();
                        nameBuilder.Append($"{file.Id}");

                        string name = null;
                        if (file.NameHash != null)
                        {
                            if (nameHashLookup.TryGetValue(file.NameHash.Value, out name) == true)
                            {
                                nameBuilder.Append($"_{name}");
                            }
                            else
                            {
                                nameBuilder.Append($"_HASH[{file.NameHash.Value:X8}]");
                            }
                        }

                        if (parent.IdCounts != null)
                        {
                            var idCounts = parent.IdCounts;
                            int idCount;
                            idCounts.TryGetValue(file.Id, out idCount);
                            idCount++;
                            idCounts[file.Id] = idCount;

                            if (idCount > 1)
                            {
                                nameBuilder.Append($"_DUP_{idCount}");
                            }
                        }

                        if (unpackNestedPacks == true && file.DataSize >= 8)
                        {
                            input.Position = file.DataOffset;
                            var fileMagic = input.ReadValueU32(Endian.Little);

                            if (fileMagic == PackFile.Signature || fileMagic.Swap() == PackFile.Signature)
                            {
                                input.Position = file.DataOffset;
                                var nestedPack = HandleNestedPack(input, fileQueue, file.Id, nameBuilder.ToString(), parent);
                                fileContainers.Add(nestedPack);
                                parent.FileManifests.Add(new FileTableManifest.File()
                                {
                                    Id = file.Id,
                                    NameHash = file.NameHash,
                                    Name = name,
                                    IsPack = true,
                                    Path = CleanPathForManifest(PathHelper.GetRelativePath(parent.BasePath, nestedPack.ManifestPath)),
                                });
                                continue;
                            }
                        }

                        var outputPath = Path.Combine(parent.BasePath, nameBuilder.ToString());

                        var outputParentPath = Path.GetDirectoryName(outputPath);
                        if (string.IsNullOrEmpty(outputParentPath) == false)
                        {
                            Directory.CreateDirectory(outputParentPath);
                        }

                        input.Position = file.DataOffset;
                        var extension = FileExtensions.Detect(input, file.DataSize);
                        outputPath = Path.ChangeExtension(outputPath, extension);

                        if (verbose == true)
                        {
                            Console.WriteLine(outputPath);
                        }

                        input.Position = file.DataOffset;
                        using (var output = File.Create(outputPath))
                        {
                            output.WriteFromStream(input, file.DataSize);
                        }

                        parent.FileManifests.Add(new FileTableManifest.File()
                        {
                            Id = file.Id,
                            NameHash = file.NameHash,
                            Name = name,
                            Path = CleanPathForManifest(PathHelper.GetRelativePath(parent.BasePath, outputPath)),
                        });
                    }
                }

                foreach (var fileContainer in fileContainers)
                {
                    WriteManifest(fileContainer.ManifestPath, fileContainer);
                }

                tableManifest.Directories.Add(new FileTableManifest.Directory()
                {
                    Id = directory.Id,
                    DataBlockSize = directory.DataBlockSize,
                    IsInInstallData = directory.IsInInstallData,
                    FileManifest = CleanPathForManifest(PathHelper.GetRelativePath(outputBasePath, tableDirectory.ManifestPath)),
                });
            }

            WriteManifest(tableManifestPath, tableManifest);
        }

        private static IFileContainer HandleNestedPack(
            Stream input,
            Queue<QueuedFile> fileQueue,
            int id,
            string name,
            IFileContainer parent)
        {
            var basePosition = input.Position;

            var packFile = new PackFile();
            packFile.Deserialize(input);

            var container = new NestedPack()
            {
                Id = id,
                BasePath = Path.Combine(parent.BasePath, name),
                Parent = parent,
            };

            var entryCount = packFile.EntryOffsets.Count;
            for (int i = 0; i < entryCount; i++)
            {
                uint entryOffset = packFile.EntryOffsets[i];
                uint nextEntryOffset = i + 1 >= entryCount
                    ? packFile.EndOffset
                    : packFile.EntryOffsets[i + 1];
                uint entrySize = nextEntryOffset - entryOffset;
                fileQueue.Enqueue(new QueuedFile()
                {
                    Id = i,
                    Parent = container,
                    DataOffset = basePosition + entryOffset,
                    DataSize = entrySize,
                });
            }

            return container;
        }

        private class QueuedFile
        {
            public int Id { get; set; }
            public IFileContainer Parent { get; set; }
            public uint? NameHash { get; set; }
            public long DataOffset { get; set; }
            public uint DataSize { get; set; }
        }

        private interface IFileContainer
        {
            int Id { get; }
            string BasePath { get; }
            string ManifestPath { get; }
            IFileContainer Parent { get; }
            Dictionary<long, int> IdCounts { get; }
            List<FileTableManifest.File> FileManifests { get; }
        }

        private class TableDirectory : IFileContainer
        {
            public TableDirectory()
            {
                this.IdCounts = new Dictionary<long, int>();
                this.FileManifests = new List<FileTableManifest.File>();
            }

            public int Id { get; set; }
            public string BasePath { get; set; }
            public string ManifestPath { get { return Path.Combine(this.BasePath, "@manifest.json"); } }
            public IFileContainer Parent { get { return null; } }
            public Dictionary<long, int> IdCounts { get; }
            public List<FileTableManifest.File> FileManifests { get; }
        }

        private class NestedPack : IFileContainer
        {
            public NestedPack()
            {
                this.FileManifests = new List<FileTableManifest.File>();
            }

            public int Id { get; set; }
            public string BasePath { get; set; }
            public string ManifestPath { get { return Path.Combine(this.BasePath, "@manifest.json"); } }
            public IFileContainer Parent { get; set; }
            public Dictionary<long, int> IdCounts { get { return null; } }
            public List<FileTableManifest.File> FileManifests { get; }
        }

        private static string CleanPathForManifest(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/')
                       .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static void WriteManifest(string path, FileTableManifest manifest)
        {
            string content;
            using (var stringWriter = new StringWriter())
            using (var writer = new JsonTextWriter(stringWriter))
            {
                writer.Indentation = 2;
                writer.IndentChar = ' ';
                writer.Formatting = Formatting.Indented;
                var serializer = new JsonSerializer();
                serializer.Serialize(writer, manifest);
                writer.Flush();
                stringWriter.Flush();
                content = stringWriter.ToString();
            }
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        private static void WriteManifest(string path, IFileContainer directory)
        {
            string content;
            using (var stringWriter = new StringWriter())
            using (var writer = new JsonTextWriter(stringWriter))
            {
                writer.Indentation = 2;
                writer.IndentChar = ' ';
                writer.Formatting = Formatting.Indented;
                writer.WriteStartArray();

                foreach (var fileManifest in directory.FileManifests)
                {
                    writer.WriteStartObject();
                    var oldFormatting = writer.Formatting;
                    writer.Formatting = Formatting.None;

                    if ((directory is NestedPack) == false)
                    {
                        writer.WritePropertyName("id");
                        writer.WriteValue(fileManifest.Id);
                        if (fileManifest.Name != null)
                        {
                            writer.WritePropertyName("name");
                            writer.WriteValue(fileManifest.Name);
                        }
                        else if (fileManifest.NameHash != null)
                        {
                            writer.WritePropertyName("name_hash");
                            writer.WriteValue(fileManifest.NameHash.Value);
                        }
                    }

                    if (fileManifest.IsPack == true)
                    {
                        writer.WritePropertyName("pack");
                        writer.WriteValue(true);
                    }

                    writer.WritePropertyName("path");
                    writer.WriteValue(fileManifest.Path);

                    writer.WriteEndObject();
                    writer.Formatting = oldFormatting;
                }

                writer.WriteEndArray();
                writer.Flush();
                stringWriter.Flush();
                content = stringWriter.ToString();
            }

            var pathParent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(pathParent) == false)
            {
                Directory.CreateDirectory(pathParent);
            }
            File.WriteAllText(path, content, Encoding.UTF8);
        }
    }
}
