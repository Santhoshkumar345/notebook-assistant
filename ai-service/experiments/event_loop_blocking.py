"""Does a blocking call inside `async def` really freeze every other request? Measured, not asserted.

One slow endpoint that blocks for 1 second, written two ways, while 5 cheap /ping requests arrive shortly after it starts.
We report WHEN each ping completes, in ms since the slow request began.

  python -m experiments.event_loop_blocking

httpx's ASGI transport runs the app on the same event loop as the caller, which is exactly the property that matters:
one loop serves everything, as it does under uvicorn.
"""

import asyncio
import time

import httpx
from fastapi import FastAPI

app = FastAPI()
BLOCK_S = 1.0


@app.get("/ping")
async def ping():
    return {"ok": True}


@app.get("/blocking-in-async")
async def blocking_in_async():
    time.sleep(BLOCK_S)  # blocks the event loop: nothing else can run until it returns
    return {"ok": True}


@app.get("/blocking-in-def")
def blocking_in_def():
    time.sleep(BLOCK_S)  # a plain `def` handler is run in a threadpool, off the event loop
    return {"ok": True}


async def measure(slow_path: str) -> tuple[list[float], float]:
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        start = time.perf_counter()

        async def timed_ping(delay: float) -> float:
            await asyncio.sleep(delay)
            await client.get("/ping")
            return (time.perf_counter() - start) * 1000

        async def slow() -> float:
            await client.get(slow_path)
            return (time.perf_counter() - start) * 1000

        slow_task = asyncio.create_task(slow())
        pings = await asyncio.gather(*(timed_ping(0.1 + 0.05 * i) for i in range(5)))
        return pings, await slow_task


async def main() -> None:
    print(f"A {BLOCK_S:.0f}s blocking endpoint, with 5 pings issued at 100-300 ms. Times are ms since the slow request began.\n")
    for path in ("/blocking-in-async", "/blocking-in-def"):
        pings, slow = await measure(path)
        print(f"{path:<20} pings completed at: {[round(p) for p in pings]}   slow request completed at: {round(slow)}")
    print("\nIf the pings of /blocking-in-async finish only after ~1000 ms, the blocking call froze the loop for everyone.")


if __name__ == "__main__":
    asyncio.run(main())
