using UnityEngine;

namespace CrowRx.Trigger.Unity
{
    /// <summary>Application-owned target selection, independent of the trigger package.</summary>
    public abstract class TriggerTargetProvider : MonoBehaviour
    {
        /// <summary>Null means the target is not ready; the condition keeps waiting.</summary>
        public abstract GameObject Target { get; }
    }
}