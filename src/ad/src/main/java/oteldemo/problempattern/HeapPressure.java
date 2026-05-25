/*
* Copyright The OpenTelemetry Authors
* SPDX-License-Identifier: Apache-2.0
*/
package oteldemo.problempattern;

import java.util.ArrayDeque;
import java.util.Deque;

import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.Logger;

/**
 * Simulates sustained heap pressure by allocating large byte chunks on every
 * invocation and keeping a bounded ring buffer of them. The constant churn
 * produces frequent minor garbage collections (sawtooth heap usage) without
 * causing OutOfMemoryError.
 */
public class HeapPressure {
    private static final Logger logger = LogManager.getLogger(HeapPressure.class.getName());
    private static final int CHUNK_SIZE_BYTES = 1024 * 1024;
    private static final int MAX_CHUNKS = 64;

    private static HeapPressure instance;

    private final Deque<byte[]> chunks = new ArrayDeque<>();
    private boolean active = false;

    public static synchronized HeapPressure getInstance() {
        if (instance == null) {
            instance = new HeapPressure();
        }
        return instance;
    }

    public void execute(boolean enabled) {
        if (enabled) {
            if (!active) {
                logger.info("Heap-pressure problempattern enabled");
                active = true;
            }
            byte[] chunk = new byte[CHUNK_SIZE_BYTES];
            for (int i = 0; i < chunk.length; i += 4096) {
                chunk[i] = (byte) i;
            }
            synchronized (chunks) {
                chunks.addLast(chunk);
                while (chunks.size() > MAX_CHUNKS) {
                    chunks.removeFirst();
                }
            }
        } else if (active) {
            synchronized (chunks) {
                chunks.clear();
            }
            active = false;
        }
    }
}
