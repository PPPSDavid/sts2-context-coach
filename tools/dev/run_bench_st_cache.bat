@echo off
set CRG_APPLY_ST_CACHE_PATCH=1
set CRG_EMBEDDING_MODEL=Qwen/Qwen3-Embedding-0.6B
"D:\miniconda\envs\sts2-context-coach\python.exe" "%~dp0bench_st_cache.py"
