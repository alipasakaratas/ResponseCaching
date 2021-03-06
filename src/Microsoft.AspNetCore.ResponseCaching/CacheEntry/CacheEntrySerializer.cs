﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using Microsoft.AspNetCore.Http;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    internal static class CacheEntrySerializer
    {
        private const int FormatVersion = 1;

        public static object Deserialize(byte[] serializedEntry)
        {
            if (serializedEntry == null)
            {
                return null;
            }

            using (var memory = new MemoryStream(serializedEntry))
            {
                using (var reader = new BinaryReader(memory))
                {
                    return Read(reader);
                }
            }
        }

        public static byte[] Serialize(object entry)
        {
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory))
                {
                    Write(writer, entry);
                    writer.Flush();
                    return memory.ToArray();
                }
            }
        }

        // Serialization Format
        // Format version (int)
        // Type (char: 'B' for CachedResponseBody, 'R' for CachedResponse, 'V' for CachedVaryRules)
        // Type-dependent data (see CachedResponse and CachedVaryRules)
        public static object Read(BinaryReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            if (reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            var type = reader.ReadChar();

            if (type == 'B')
            {
                return ReadCachedResponseBody(reader);
            }
            else if (type == 'R')
            {
                return ReadCachedResponse(reader);
            }
            else if (type == 'V')
            {
                return ReadCachedVaryRules(reader);
            }

            // Unable to read as CachedResponse or CachedVaryRules
            return null;
        }

        // Serialization Format
        // Body length (int)
        // Body (byte[])
        private static CachedResponseBody ReadCachedResponseBody(BinaryReader reader)
        {
            var bodyLength = reader.ReadInt32();
            var body = reader.ReadBytes(bodyLength);

            return new CachedResponseBody() { Body = body };
        }

        // Serialization Format
        // BodyKeyPrefix (string)
        // Creation time - UtcTicks (long)
        // Status code (int)
        // Header count (int)
        // Header(s)
        //   Key (string)
        //   Value (string)
        // ContainsBody (bool)
        //   Body length (int)
        //   Body (byte[])
        private static CachedResponse ReadCachedResponse(BinaryReader reader)
        {
            var bodyKeyPrefix = reader.ReadString();
            var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var statusCode = reader.ReadInt32();
            var headerCount = reader.ReadInt32();
            var headers = new HeaderDictionary();
            for (var index = 0; index < headerCount; index++)
            {
                var key = reader.ReadString();
                var value = reader.ReadString();
                headers[key] = value;
            }

            var containsBody = reader.ReadBoolean();
            int bodyLength;
            byte[] body = null;
            if (containsBody)
            {
                bodyLength = reader.ReadInt32();
                body = reader.ReadBytes(bodyLength);
            }

            return new CachedResponse { BodyKeyPrefix = bodyKeyPrefix, Created = created, StatusCode = statusCode, Headers = headers, Body = body };
        }

        // Serialization Format
        // Guid (long)
        // Headers count
        // Header(s) (comma separated string)
        // Params count
        // Param(s) (comma separated string)
        private static CachedVaryRules ReadCachedVaryRules(BinaryReader reader)
        {
            var varyKeyPrefix = reader.ReadString();

            var headerCount = reader.ReadInt32();
            var headers = new string[headerCount];
            for (var index = 0; index < headerCount; index++)
            {
                headers[index] = reader.ReadString();
            }
            var paramCount = reader.ReadInt32();
            var param = new string[paramCount];
            for (var index = 0; index < paramCount; index++)
            {
                param[index] = reader.ReadString();
            }

            return new CachedVaryRules { VaryKeyPrefix = varyKeyPrefix, Headers = headers, Params = param };
        }

        // See serialization format above
        public static void Write(BinaryWriter writer, object entry)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            writer.Write(FormatVersion);

            if (entry is CachedResponseBody)
            {
                writer.Write('B');
                WriteCachedResponseBody(writer, entry as CachedResponseBody);
            }
            else if (entry is CachedResponse)
            {
                writer.Write('R');
                WriteCachedResponse(writer, entry as CachedResponse);
            }
            else if (entry is CachedVaryRules)
            {
                writer.Write('V');
                WriteCachedVaryRules(writer, entry as CachedVaryRules);
            }
            else
            {
                throw new NotSupportedException($"Unrecognized entry format for {nameof(entry)}.");
            }
        }

        // See serialization format above
        private static void WriteCachedResponseBody(BinaryWriter writer, CachedResponseBody entry)
        {
            writer.Write(entry.Body.Length);
            writer.Write(entry.Body);
        }

        // See serialization format above
        private static void WriteCachedResponse(BinaryWriter writer, CachedResponse entry)
        {
            writer.Write(entry.BodyKeyPrefix);
            writer.Write(entry.Created.UtcTicks);
            writer.Write(entry.StatusCode);
            writer.Write(entry.Headers.Count);
            foreach (var header in entry.Headers)
            {
                writer.Write(header.Key);
                writer.Write(header.Value);
            }

            if (entry.Body == null)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                writer.Write(entry.Body.Length);
                writer.Write(entry.Body);
            }
        }

        // See serialization format above
        private static void WriteCachedVaryRules(BinaryWriter writer, CachedVaryRules varyRules)
        {
            writer.Write(varyRules.VaryKeyPrefix);

            writer.Write(varyRules.Headers.Count);
            foreach (var header in varyRules.Headers)
            {
                writer.Write(header);
            }

            writer.Write(varyRules.Params.Count);
            foreach (var param in varyRules.Params)
            {
                writer.Write(param);
            }
        }
    }
}
