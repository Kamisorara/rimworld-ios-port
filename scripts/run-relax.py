# 一次性启动器：工具调用层会折叠 Bash 参数里的空格（连 python -c 的字符串都不放过），
# dotnet exec 类调用改由本文件执行。用法：python scripts/run-relax.py <relaxed-dir>
import subprocess
import sys

TOOL = r"D:\Data\Code\reverse\rimworld-ios-port\scripts\relax-initonly\bin\Release\net10.0\relax-initonly.dll"
sys.exit(subprocess.run(["dotnet", TOOL, sys.argv[1] if len(sys.argv) > 1 else "game-stage-relaxed"]).returncode)
