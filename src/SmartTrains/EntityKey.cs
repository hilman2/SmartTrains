using Unity.Entities;

namespace SmartTrains
{
    /// <summary>
    /// Turns entities into the 64-bit keys the core works with, and back.
    /// Index and version both go in, so a key never matches a later entity
    /// that reuses the index.
    /// </summary>
    internal static class EntityKey
    {
        public static long Of(Entity entity)
        {
            return ((long)entity.Index << 32) | (uint)entity.Version;
        }

        public static Entity ToEntity(long key)
        {
            return new Entity { Index = (int)(key >> 32), Version = (int)(key & 0xffffffffL) };
        }
    }
}
