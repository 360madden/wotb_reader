import re, sys

path = sys.argv[1]
src = open(path, encoding='utf-8').read()

# Strip block comments --[[ ... ]]
s = re.sub(r'--\[(=*)\[.*?\]\1\]', '', src, flags=re.S)
# Strip line comments
s = re.sub(r'--[^\n]*', '', s)
# Replace string literals (keep it crude: double and single quotes)
s = re.sub(r'"([^"\\]|\\.)*"', '""', s)
s = re.sub(r"'([^'\\]|\\.)*'", "''", s)

openers = ('function', 'if', 'for', 'while')
tokens = re.findall(r'\b(function|if|then|for|while|do|end|elseif|else)\b', s)
depth = 0
balanced = True
maxdepth = 0
for t in tokens:
    if t in openers:
        depth += 1
        maxdepth = max(maxdepth, depth)
    elif t == 'end':
        depth -= 1
        if depth < 0:
            balanced = False
            print('NEGATIVE DEPTH at token', t)
            break
print(f'openers/closers depth final={depth} max={maxdepth} balanced={balanced and depth == 0}')
if not (balanced and depth == 0):
    sys.exit(1)
