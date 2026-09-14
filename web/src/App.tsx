import { Route, Routes } from 'react-router'
import Dashboard from '@/routes/Dashboard'
import PatchDetail from '@/routes/PatchDetail'

function App() {
  return (
    <Routes>
      <Route path="/" element={<Dashboard />} />
      <Route path="/patches/:ns/:type/:patchId" element={<PatchDetail />} />
    </Routes>
  )
}

export default App
