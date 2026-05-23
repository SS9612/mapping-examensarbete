/* global process */
import express from 'express';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import { readFileSync } from 'fs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const app = express();
const PORT = process.env.PORT || 3000;

app.use(express.static(join(__dirname, 'dist')));

app.get('*', (req, res) => {
  if (req.path.startsWith('/api')) {
    return res.status(404).send('Not found');
  }
  
  const indexPath = join(__dirname, 'dist', 'index.html');
  try {
    const indexContent = readFileSync(indexPath, 'utf-8');
    res.send(indexContent);
  } catch {
    res.status(404).send('Frontend not built. Please run npm run build first.');
  }
});

app.listen(PORT, () => {
  console.log(`Server is running on port ${PORT}`);
});