using System;
using UnityEngine;

namespace MiniConsole.Samples
{
    /// <summary>
    /// MiniConsole の動作確認用。Play 開始時に NullReferenceException を発生させる。
    /// </summary>
    public sealed class NullReferenceTester : MonoBehaviour
    {
        sealed class Player
        {
            public string name;
        }

        Player player;

        [SerializeField] private bool _update;

        void Start()
        {
            Debug.Log("player.name.Length");
            ThrowNullReference();
        }

        private void Update()
        {
            if (!_update)
                return;
            ThrowNullReference();
        }

        [ContextMenu("Throw NullReferenceException")]
        void ThrowNullReference()
        {
            Debug.Log(player.name.Length);
        }
    }
}
