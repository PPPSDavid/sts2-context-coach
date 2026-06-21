"""Run via run_bench_st_cache.bat (sets env before Python; .pth loads crg_st_model_cache)."""
import time

from code_review_graph.embeddings import LocalEmbeddingProvider

t0 = time.perf_counter()
LocalEmbeddingProvider().embed_query("hello")
print("first_provider_s", round(time.perf_counter() - t0, 3))
t0 = time.perf_counter()
LocalEmbeddingProvider().embed_query("world")
print("second_provider_s", round(time.perf_counter() - t0, 3))
