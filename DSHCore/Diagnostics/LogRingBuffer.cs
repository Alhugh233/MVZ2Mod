using System;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 定长环形缓冲：只保留最近 <see cref="Capacity"/> 条日志。
    ///
    /// <para>网页查看器的新客户端要能立刻看到「最近发生了什么」，所以必须留一份内存
    /// 历史，而不能只有实时流。</para>
    ///
    /// <para>为什么不用 <c>ConcurrentQueue</c> + 超限出队：那样计数与出队不是原子的，
    /// 需要一个额外的计数器和 CAS 循环。环形缓冲用一把锁就够了 —— 日志量远达不到
    /// 需要无锁的程度，而「读快照」也必须原子，否则会读到半新半旧的数据。</para>
    /// </summary>
    internal sealed class LogRingBuffer
    {
        private readonly LogRecord[] _items;
        private readonly object _gate = new object();
        private int _start;     // 最旧一条的下标
        private int _count;

        public LogRingBuffer(int capacity)
        {
            if (capacity < 1)
                capacity = 1;
            _items = new LogRecord[capacity];
        }

        public int Capacity => _items.Length;

        public int Count
        {
            get { lock (_gate) { return _count; } }
        }

        public void Add(LogRecord record)
        {
            if (record == null)
                return;

            lock (_gate)
            {
                if (_count == _items.Length)
                {
                    // 满：覆盖最旧的一条
                    _items[_start] = record;
                    _start = (_start + 1) % _items.Length;
                }
                else
                {
                    _items[(_start + _count) % _items.Length] = record;
                    _count++;
                }
            }
        }

        /// <summary>
        /// 取出最近 <paramref name="limit"/> 条（从旧到新）。
        /// <paramref name="limit"/> &lt;= 0 表示全部。
        /// </summary>
        public LogRecord[] Snapshot(int limit)
        {
            lock (_gate)
            {
                int take = _count;
                if (limit > 0 && limit < take)
                    take = limit;

                var result = new LogRecord[take];
                int skip = _count - take;
                for (int i = 0; i < take; i++)
                {
                    result[i] = _items[(_start + skip + i) % _items.Length];
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_items, 0, _items.Length);
                _start = 0;
                _count = 0;
            }
        }
    }
}
