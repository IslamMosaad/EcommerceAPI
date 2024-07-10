﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using MessagePack;
using MessagePack.Resolvers;

namespace EcommerceAPI.Services.cacheServices.sharedCache
{
    public static class MemoryMappedFileCache<T> where T : class
    {
        private static readonly string MapName = "SharedMemoryMap";
        private static readonly long MapSize = 10 * 1024 * 1024; // 10 MB
        private static readonly object LockObject = new object();

        public static void Set(string key, T value, TimeSpan expiration)
        {
            var cacheItem = new CacheItem<T>(value, DateTime.UtcNow.Add(expiration));

            lock (LockObject)
            {
                using (var mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize))
                {
                    var data = ReadAllEntries(mmf);
                    data[key] = cacheItem;
                    WriteAllEntries(mmf, data);
                }
            }
        }

        public static T Get(string key)
        {
            lock (LockObject)
            {
                using (var mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize))
                {
                    var data = ReadAllEntries(mmf);
                    if (data.TryGetValue(key, out var cacheItem) && cacheItem.Expiration > DateTime.UtcNow)
                    {
                        return cacheItem.Value;
                    }
                    else
                    {
                        Remove(key); // Automatically remove expired item if found
                        return null;
                    }
                }
            }
        }

        public static bool Remove(string key)
        {
            lock (LockObject)
            {
                using (var mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize))
                {
                    var data = ReadAllEntries(mmf);
                    if (data.Remove(key))
                    {
                        WriteAllEntries(mmf, data);
                        return true;
                    }
                    return false;
                }
            }
        }

        public static void RemoveExpired()
        {
            lock (LockObject)
            {
                using (var mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize))
                {
                    var data = ReadAllEntries(mmf);
                    var expiredKeys = data.Where(kv => kv.Value.Expiration <= DateTime.UtcNow)
                                          .Select(kv => kv.Key)
                                          .ToList();

                    foreach (var key in expiredKeys)
                    {
                        data.Remove(key);
                    }

                    WriteAllEntries(mmf, data);
                }
            }
        }

        private static Dictionary<string, CacheItem<T>> ReadAllEntries(MemoryMappedFile mmf)
        {
            var data = new Dictionary<string, CacheItem<T>>();

            try
            {
                using (var accessor = mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.Read))
                {
                    accessor.Read(0, out long length);
                    Console.WriteLine($"Length read: {length}");

                    if (length <= 0 || length > MapSize - 8)
                    {
                        Console.WriteLine("Invalid length read from memory-mapped file.");
                        return data;
                    }

                    var bytes = new byte[length];
                    accessor.ReadArray(8, bytes, 0, (int)length); // Read starting from offset 8 to skip the length value itself

                    var options = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
                    data = MessagePackSerializer.Deserialize<Dictionary<string, CacheItem<T>>>(bytes, options);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading cache entries from memory-mapped file: {ex.Message}");
                throw; // Rethrow or handle the exception as per your application's error handling strategy
            }

            return data;
        }

        private static void WriteAllEntries(MemoryMappedFile mmf, Dictionary<string, CacheItem<T>> data)
        {
            try
            {
                byte[] bytes;
                var options = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
                using (var memoryStream = new MemoryStream())
                {
                    MessagePackSerializer.Serialize(memoryStream, data, options);
                    bytes = memoryStream.ToArray();
                }

                Console.WriteLine($"Bytes to write: {bytes.Length}");

                using (var accessor = mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.Write))
                {
                    // Ensure the length is written at the beginning (offset 0)
                    accessor.Write(0, bytes.LongLength);
                    Console.WriteLine($"Length written: {bytes.LongLength}");

                    // Write starting from offset 8 to store the length of bytes
                    accessor.WriteArray(8, bytes, 0, bytes.Length);

                    // Read back to verify write success
                    accessor.Read(0, out long writtenLength);
                    Console.WriteLine($"Written length read back: {writtenLength}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing cache entries to memory-mapped file: {ex.Message}");
                throw; // Rethrow or handle the exception as per your application's error handling strategy
            }
        }

        [MessagePackObject]
        public class CacheItem<T>
        {
            [Key(0)]
            public T Value { get; set; }

            [Key(1)]
            public DateTime Expiration { get; set; }

            public CacheItem() { } // Parameterless constructor is required for serialization

            public CacheItem(T value, DateTime expiration)
            {
                Value = value;
                Expiration = expiration;
            }
        }
    }
}
