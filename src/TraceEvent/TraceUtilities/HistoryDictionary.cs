//     Copyright (c) Microsoft Corporation.  All rights reserved.
using System.Collections.Generic;
using System.Diagnostics;
using Address = System.UInt64;

namespace Microsoft.Diagnostics.Tracing.Utilities
{
    // Utilities for TraceEventParsers
    /// <summary>
    /// A HistoryDictionary is designed to look up 'handles' (pointer sized quantities), that might get reused
    /// over time (eg Process IDs, thread IDs).  Thus it takes a handle AND A TIME, and finds the value
    /// associated with that handle at that time.   
    /// </summary>
    internal class HistoryDictionary<T>
    {
        public HistoryDictionary(int initialSize)
        {
            entries = new Dictionary<long, HistoryValue>(initialSize);
        }
        /// <summary>
        /// Adds the association that 'id' has the value 'value' from 'startTime100ns' ONWARD until
        /// it is supersede by the same id being added with a time that is after this.   Thus if
        /// I did Add(58, 1000, MyValue1), and add(58, 500, MyValue2) 'TryGetValue(58, 750, out val) will return
        /// MyValue2 (since that value is 'in force' between time 500 and 1000.   
        /// </summary>
        public void Add(Address id, long startTime, T value, bool isEndRundown = false)
        {
            HistoryValue entry;
            if (!entries.TryGetValue((long)id, out entry))
            {
                // rundown events are 'last chance' events that we only add if we don't already have an entry for it.  
                if (isEndRundown)
                    startTime = 0;
                entries.Add((long)id, new HistoryValue(startTime, id, value));
            }
            else
            {
                Debug.Assert(entry != null);
                var firstEntry = entry;

                // See if we can jump ahead.  Currently we only do this of the first entry, 
                // But you could imagine using some of the other nodes's skipAhead entries.   
                if (firstEntry.skipAhead != null && firstEntry.skipAhead.startTime < startTime)
                    entry = firstEntry.skipAhead;

                for (; ; )
                {
                    if (entry.next == null)
                    {
                        entry.next = new HistoryValue(startTime, id, value);
                        break;
                    }

                    // We sort the entries from smallest to largest time. 
                    if (startTime < entry.startTime)
                    {
                        // This entry belongs in front of this entry.  
                        // Insert it before the current entry by moving the current entry after it.  
                        HistoryValue newEntry = new HistoryValue(entry);
                        entry.startTime = startTime;
                        entry.value = value;
                        entry.next = newEntry;
                        Debug.Assert(entry.startTime <= entry.next.startTime);
                        break;
                    }
                    entry = entry.next;
                }
                firstEntry.skipAhead = entry.next;
            }
            count++;
        }
        // TryGetValue will return the value associated with an id that was placed in the stream 
        // at time100ns OR BEFORE.  
        public bool TryGetValue(Address id, long time, out T value)
        {
            HistoryValue entry;
            if (entries.TryGetValue((long)id, out entry))
            {
                // The entries are sorted smallest to largest.  
                // We want the last entry that is smaller (or equal) to the target time) 

                var firstEntry = entry;
                // See if we can jump ahead.  Currently we only do this of the first entry, 
                // But you could imagine using some of the other nodes's skipAhead entries.   
                if (firstEntry.skipAhead != null && firstEntry.skipAhead.startTime < time)
                    entry = firstEntry.skipAhead;

                HistoryValue last = null;
                for (; ; )
                {
                    if (time < entry.startTime)
                        break;
                    last = entry;
                    entry = entry.next;
                    if (entry == null)
                        break;
                }
                if (last != null)
                {
                    value = last.value;
                    firstEntry.skipAhead = last;
                    return true;
                }
            }
            value = default(T);
            return false;
        }
        public IEnumerable<HistoryValue> Entries
        {
            get
            {
#if DEBUG
            int ctr = 0;
#endif
                foreach (HistoryValue entry in entries.Values)
                {
                    HistoryValue list = entry;
                    while (list != null)
                    {
#if DEBUG
                    ctr++;
#endif
                        yield return list;
                        list = list.next;
                    }
                }
#if DEBUG
            Debug.Assert(ctr == count);
#endif
            }
        }
        public int Count { get { return count; } }
        /// <summary>
        /// Remove all entries associated with a given key (over all time).  
        /// </summary>
        public void Remove(Address id)
        {
            HistoryValue entry;
            if (entries.TryGetValue((long)id, out entry))
            {
                // Fix up the count by the number of entries we remove.  
                while (entry != null)
                {
                    entry.skipAhead = null;     // Throw away optimization data.
                    --count;
                    entry = entry.next;
                }
                entries.Remove((long)id);
            }
        }

        public class HistoryValue
        {
            public Address Key { get { return key; } }
            public long StartTime { get { return startTime; } }
            public T Value { get { return value; } }
            #region private
            internal HistoryValue(HistoryValue entry)
            {
                this.key = entry.key;
                this.startTime = entry.startTime;
                this.value = entry.value;
                this.next = entry.next;
            }
            internal HistoryValue(long startTime100ns, Address key, T value)
            {
                this.key = key;
                this.startTime = startTime100ns;
                this.value = value;
            }

            internal Address key;
            internal long startTime;
            internal T value;
            internal HistoryValue next;
            // To improve getting to the end quickly, we allow nodes to store values that 'skip ahead'.
            // Today we only use this field for the first node to skip to the end (for fast append) 
            // The only strong invarient for this field is that it point further up the same list.  
            internal HistoryValue skipAhead;   
            #endregion
        }
        #region private
        Dictionary<long, HistoryValue> entries;
        int count;
        #endregion
    }
}
