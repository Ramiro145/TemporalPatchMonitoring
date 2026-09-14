import { Route, Routes } from 'react-router'
import Dashboard from '@/routes/Dashboard'
import PatchDetail from '@/routes/PatchDetail'
import Runs from '@/routes/Runs'

function App() {
  return (
    <Routes>
      <Route path="/" element={<Dashboard />} />
      <Route path="/patches/:ns/:type/:patchId" element={<PatchDetail />} />
      <Route path="/runs" element={<Runs />} />
    </Routes>
  )
}

export default App
