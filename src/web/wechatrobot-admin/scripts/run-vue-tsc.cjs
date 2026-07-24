const path = require('node:path');

const aliasedTypeScriptRoot = path.dirname(require.resolve('typescript-vue/package.json'));
const tscPath = path.join(aliasedTypeScriptRoot, 'lib', 'tsc.js');

require('vue-tsc').run(tscPath);
