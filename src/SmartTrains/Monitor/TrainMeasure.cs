using Colossal.Entities;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace SmartTrains.Monitor
{
    /// <summary>
    /// A train's size and pace, from its cars' prefabs. Top speed,
    /// acceleration and braking are each those of the weakest car, as
    /// TrainNavigationSystem combines them; the length is the sum of the
    /// cars' attach offsets, as VehicleUtils.CalculateLength has it.
    /// </summary>
    internal struct TrainMeasure
    {
        /// <summary>Cars with train data; 0 if none was found, and then nothing else is known either.</summary>
        public int Cars;

        public float Length;
        public float MaxSpeed;
        public float Acceleration;
        public float Braking;

        public static TrainMeasure Of(EntityManager em, Entity train)
        {
            var measure = new TrainMeasure { MaxSpeed = float.MaxValue, Acceleration = float.MaxValue, Braking = float.MaxValue };
            DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(train, true);
            for (int i = 0; i < layout.Length; i++)
            {
                Entity prefab = em.GetComponentData<PrefabRef>(layout[i].m_Vehicle).m_Prefab;
                if (!em.TryGetComponent(prefab, out TrainData data))
                    continue;
                measure.Cars++;
                measure.Length += math.csum(data.m_AttachOffsets);
                measure.MaxSpeed = math.min(measure.MaxSpeed, data.m_MaxSpeed);
                measure.Acceleration = math.min(measure.Acceleration, data.m_Acceleration);
                measure.Braking = math.min(measure.Braking, data.m_Braking);
            }
            if (measure.Cars == 0)
                return default;
            return measure;
        }
    }
}
