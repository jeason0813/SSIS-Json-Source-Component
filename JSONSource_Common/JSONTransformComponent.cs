﻿using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Runtime.Wrapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Threading.Tasks;
#if LINQ_SUPPORTED
using System.Linq;
#endif


namespace com.webkingsoft.JSONSource_Common
{
#if DTS120
    [DtsPipelineComponent(DisplayName = "JSON Filter", Description = "Parses json data from an input column", ComponentType = ComponentType.Transform, UITypeName = "com.webkingsoft.JSONSource_Common.JSONTransformComponentUI,com.webkingsoft.JSONSource_120,Version=1.0.200.0,Culture=neutral", IconResource = "com.webkingsoft.JSONSource_120.jsource.ico")]
#elif DTS110
    [DtsPipelineComponent(DisplayName = "JSON Filter", Description = "Parses json data from an input column", ComponentType = ComponentType.Transform, UITypeName = "com.webkingsoft.JSONSource_Common.JSONTransformComponentUI,com.webkingsoft.JSONSource_110,Version=1.0.200.0,Culture=neutral", IconResource = "com.webkingsoft.JSONSource_110.jsource.ico")]
#endif

    public class JSONTransformComponent : PipelineComponent
    {
        public const string JSON_SOURCE_DEBUG_VAR = "wk_debug";
        public const int WARNING_CUSTOM_TEMP_DIR_INVALID = 11;
        public const int ERROR_NO_INPUT_SUPPORTED = 1;
        
        public const int ERROR_IOMAP_EMPTY = 14;
        public const int ERROR_IOMAP_ENTRY_ERROR = 15;
        public const int ERROR_SINGLE_OUTPUT_SUPPORTED = 16;
        public const int ERROR_INPUT_LANE_NOT_FOUND = 17;
        public const int ERROR_SELECT_TOKEN = 1001;
        public const int RUNTIME_ERROR_MODEL_INVALID = 100;
        public const int RUNTIME_GENERIC_ERROR = 1000;

        public static readonly string PROPERTY_KEY_MODEL = "CONFIGURATION_MODEL_OBJECT";
        public override void ProvideComponentProperties()
        {
            ComponentMetaData.Name = "JSON filter";
            ComponentMetaData.Description = "Given a input, this component will parse and process input text putting data into buffers.";
            ComponentMetaData.ContactInfo = "Alberto Geniola, albertogeniola@gmail.com";

            // Pulisco gli input, output e le custom properties
            base.RemoveAllInputsOutputsAndCustomProperties();
            //ComponentMetaData.UsesDispositions = false; // Non supportiamo uscite con errori
            
            // Configuro l'input
            var input = ComponentMetaData.InputCollection.New();
            input.Name = "Json Input";

            // Configuro gli output (per default nessun output presente)
            var output = ComponentMetaData.OutputCollection.New();
            output.Name = "Parsed Json lines";
            output.SynchronousInputID = 0;

            TransformationModel m = null;
            try
            {
                m = GetModel();
            }
            catch (ModelNotFoundException e)
            {
                // Non l'ho trovato. Aggiungi la proprietà, salvando l'oggetto MODEL serializzato
                // in XML. 
                var model = ComponentMetaData.CustomPropertyCollection.New();
                model.Description = "Contains information about the confiuguration of the item.";
                model.Name = PROPERTY_KEY_MODEL;
                model.Value = new TransformationModel().ToJsonConfig();
            }
        }

        public override IDTSOutput100 InsertOutput(DTSInsertPlacement insertPlacement, int outputID)
        {
            throw new Exception("This component doesn't support any additional output");
        }

        public override IDTSInput100 InsertInput(DTSInsertPlacement insertPlacement, int inputID)
        {
            throw new Exception("This component doesn't support any additional input");
        }

        public override void DeleteInput(int inputID)
        {
            throw new Exception("You cannot delete the input lane");
        }

        public override void DeleteOutput(int outputID)
        {
            throw new Exception("You cannot delete the output lane");
        }
        
        /*
        public override void OnInputPathDetached(int inputID)
        {
            base.OnInputPathDetached(inputID);
        }
        */

        /**
         * Questo metodo è invocato diverse volte durante il designtime. Al suo interno verifico che i metadati siano 
         * coerenti e consistenti. In caso di ambiguità o lacune, segnalo al designer le situazioni di inconsistenza,
         * generando opportunamente Warning o Errors.
        **/
        public override Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus Validate()
        {
            if (ComponentMetaData.AreInputColumnsValid == false)
                return DTSValidationStatus.VS_NEEDSNEWMETADATA;

            bool fireAgain = false;

            // Validazione di base
            // - Una sola linea di output.
            // - Nessuna linea di input.
            if (ComponentMetaData.InputCollection.Count != 1)
            {
                ComponentMetaData.FireError(ERROR_NO_INPUT_SUPPORTED, ComponentMetaData.Name, "This component requires at least one output lane.", null, 0, out fireAgain);
                return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
            }

            if (ComponentMetaData.OutputCollection.Count != 1)
            {
                ComponentMetaData.FireError(ERROR_SINGLE_OUTPUT_SUPPORTED, ComponentMetaData.Name, "This component only supports a single output lane.", null, 0, out fireAgain);
                return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
            }

            foreach (IDTSInputColumn100 input in ComponentMetaData.InputCollection[0].InputColumnCollection) {
                bool cancel;
                if (input.DataType != DataType.DT_STR && input.DataType != DataType.DT_WSTR && input.DataType != DataType.DT_TEXT && input.DataType != DataType.DT_NTEXT)
                {
                    ComponentMetaData.FireError(0, input.IdentificationString, "The column data type of " + input.Name + " is not a textual one, so it is unsupported.","",0,out cancel);
                    return DTSValidationStatus.VS_ISBROKEN;
                }
            }

            TransformationModel m = null;
            try
            {
                m = GetModel();
            }
            catch (Exception e)
            {
                return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
            }

            // Controlla la tabella di IO
            // Il modello è vuoto?
            var enumerator = m.IoMap.GetEnumerator();
            if (m.IoMap == null || enumerator.MoveNext() == false)
            {
                ComponentMetaData.FireError(ERROR_IOMAP_EMPTY, ComponentMetaData.Name, "This component must at least have one output column.", null, 0, out fireAgain);
                return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
            }

            // Assicurati di avere tutte le informazioni per ogni colonna
            foreach (IOMapEntry e in m.IoMap)
            {
                // FieldName and outputFiledName cannot be null, empty and must be unique.
                if (string.IsNullOrEmpty(e.InputFieldPath))
                {
                    ComponentMetaData.FireError(ERROR_IOMAP_ENTRY_ERROR, ComponentMetaData.Name, "One row of the Input-Output mapping is invalid: null or empty input field name. Please review IO configuration.", null, 0, out fireAgain);
                    return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
                }
                if (string.IsNullOrEmpty(e.OutputColName))
                {
                    ComponentMetaData.FireError(ERROR_IOMAP_ENTRY_ERROR, ComponentMetaData.Name, "One row of the Input-Output mapping is invalid: null or empty output field name. Please review IO configuration.", null, 0, out fireAgain);
                    return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
                }
                // Checks for unique cols
                foreach (IOMapEntry e1 in m.IoMap)
                {
                    if (!ReferenceEquals(e, e1) && e.InputFieldPath == e1.InputFieldPath)
                    {
                        // Not unique!
                        ComponentMetaData.FireError(ERROR_IOMAP_ENTRY_ERROR, ComponentMetaData.Name, "There are two or more rows with same InputFieldName. This is not allowed.", null, 0, out fireAgain);
                        return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
                    }
                    if (!ReferenceEquals(e, e1) && e.OutputColName == e1.OutputColName)
                    {
                        // Not unique!
                        ComponentMetaData.FireError(ERROR_IOMAP_ENTRY_ERROR, ComponentMetaData.Name, "There are two or more rows with same OutputColName. This is not allowed.", null, 0, out fireAgain);
                        return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
                    }
                }
            }

            // Controllo i parametri avanzati
            if (!string.IsNullOrEmpty(m.CustomLocalTempDir))
            {
                // Give warning only if the user specified a custom value and that one is invalid
                if (!Directory.Exists(m.CustomLocalTempDir))
                {
                    ComponentMetaData.FireWarning(WARNING_CUSTOM_TEMP_DIR_INVALID, ComponentMetaData.Name, "The path to " + m.CustomLocalTempDir + " doesn't exists on this FS. If you're going to deploy the package on another server, make sure the path is correct and the service has write permission on it.", null, 0);
                    return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
                }
            }

            // Controllo che la colonna di input sia ancora valida
            bool found = false;
            if (String.IsNullOrEmpty(m.InputColumnName))
            {
                ComponentMetaData.FireError(ERROR_INPUT_LANE_NOT_FOUND,ComponentMetaData.Name, "Input column has not been selected.", null, 0, out fireAgain);
                return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
            }
            foreach (IDTSVirtualInputColumn100 vcol in ComponentMetaData.InputCollection[0].GetVirtualInput().VirtualInputColumnCollection) {
                if (vcol.Name == m.InputColumnName) {
                    found = true;
                    break;
                }
            }

            if (!found) {
                ComponentMetaData.FireError(ERROR_INPUT_LANE_NOT_FOUND, ComponentMetaData.Name, "Input column "+m.InputColumnName+" is not present among the inputs of this component. Please update the component configuration.", null, 0, out fireAgain);
                return Microsoft.SqlServer.Dts.Pipeline.Wrapper.DTSValidationStatus.VS_ISBROKEN;
            }
            return base.Validate();
        }

        public override void ReinitializeMetaData()
        {
            ComponentMetaData.RemoveInvalidInputColumns();
            base.ReinitializeMetaData();
        }

        private TransformationModel GetModel()
        {
            TransformationModel m = null;
            bool found = false;
            // Recupera i metadati. Se non sono presenti, ritorna uno stato di invalido
            foreach (dynamic prop in ComponentMetaData.CustomPropertyCollection)
                if (prop.Name == PROPERTY_KEY_MODEL)
                {
                    // Trovato!
                    found = true;
                    m = TransformationModel.LoadFromJson((string)prop.Value);
                    break;
                }

            if (!found)
                throw new ModelNotFoundException();
            else
                return m;
        }


        // Le seguenti variabili contengono gli oggetti da usare a runtime, instanziati dal metodo seguente,
        // invocato appena prima di processare l'input.
        private IOMapEntry[] _iomap;
        private Dictionary<string, int> _outColsMaps;
        private string _pathToArray = null;
        private ParallelOptions _opt;
        private int _inputColIndex;

        public override void PreExecute()
        {
            bool debugging = false;
            IDTSVariables100 vars = null;
            try
            {
                VariableDispenser.LockOneForRead(JSON_SOURCE_DEBUG_VAR, ref vars);
                object o = vars[JSON_SOURCE_DEBUG_VAR].Value;
                if (o != null)
                    if ((bool)o)
                        debugging = true;
            }
            catch(Exception e){
                //Do nothing
                bool fireAgain = false;
                ComponentMetaData.FireInformation(0, ComponentMetaData.Name, "wk_debug variable cannot be found. I won't stop to let debug attachment.", null,0,ref fireAgain);
            }
            finally
            {
                if (vars != null)
                    vars.Unlock();
            }

            if (debugging)
                MessageBox.Show("Start Debugger");

            TransformationModel m = GetModel();
            _opt = new ParallelOptions();
            _opt.MaxDegreeOfParallelism = 4;

            bool cancel = false;
            // Carico i dettagli dal model
            try{
                m = GetModel();
            }catch(ModelNotFoundException ex) {
                ComponentMetaData.FireError(RUNTIME_ERROR_MODEL_INVALID, ComponentMetaData.Name, "Invalid Metadata for this component.", null, 0, out cancel);
                return;
            }

            // Salva il mapping in un array locale
            _iomap = m.IoMap.ToArray<IOMapEntry>();
            
            // Salva una copia locale del percorso cui attingere l'array
            _pathToArray = m.JsonObjectRelativePath;

            // Genera un dizionario ad accesso veloce per il nome della colonna: mappo nome colonna - Indice della colonna nella riga
            _outColsMaps = new Dictionary<string, int>();
            foreach (IOMapEntry e in _iomap)
            {
                bool found = false;
                foreach (IDTSOutputColumn100 col in base.ComponentMetaData.OutputCollection[0].OutputColumnCollection)
                {

                    if (col.Name == e.OutputColName)
                    {
                        found = true;
                        int colIndex = BufferManager.FindColumnByLineageID(ComponentMetaData.OutputCollection[0].Buffer, col.LineageID);
                        _outColsMaps.Add(e.OutputColName, colIndex);
                        break;
                    }
                }
                if (!found)
                {
                    // Una colonna del model non ha trovato il corrispettivo nel componente attuale
                    ComponentMetaData.FireError(RUNTIME_ERROR_MODEL_INVALID, ComponentMetaData.Name, "The component is unable to locate the column named " + e.OutputColName + " inside the component metadata. Please review the component.", null, 0, out cancel);
                    return;
                }
            }

            _inputColIndex = BufferManager.FindColumnByLineageID(ComponentMetaData.InputCollection[0].Buffer, ComponentMetaData.InputCollection[0].InputColumnCollection[0].LineageID);

        }

        private string DownloadJsonFile(string url, string customLocalTempDir = null)
        {
            string localTmp = null;
            string filePath = null;
            if (!string.IsNullOrEmpty(customLocalTempDir))
            {
                if (!Directory.Exists(customLocalTempDir))
                    throw new ArgumentException("Local tmp path doesn't exist: " + customLocalTempDir);
                localTmp = customLocalTempDir;
            }
            else
            {
                localTmp = Path.GetTempPath();
            }

            filePath = Path.Combine(localTmp, Guid.NewGuid().ToString() + ".json");

            using (StreamWriter sr = File.CreateText(Path.Combine(localTmp, Guid.NewGuid().ToString()) + ".txt"))
            {
                using (WebClient webClient = new WebClient())
                {
                    try
                    {
                        webClient.DownloadFile(url, filePath);
                        return filePath;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("Cannot download the json file from " + url + " to " + filePath, ex);
                    }
                }

            }
        }

        private PipelineBuffer _outputBuffer;
        public override void PrimeOutput(int outputs, int[] outputIDs, PipelineBuffer[] buffers)
        {
            if (buffers.Length != 0)
                _outputBuffer = buffers[0];
        }
        public override void ProcessInput(int inputID, PipelineBuffer buffer)
        {
            bool cancel = false;
            ComponentMetaData.FireInformation(1000, ComponentMetaData.Name, "PROCESS INPUT CALLED", null, 0, ref cancel);

            while (buffer.NextRow()) {
                try
                {
                    //TODO: il buffer di input arriva completo, e non con le sole colonne mappate. 
                    // Process data according to IOMappings
                    //(GetModel().InputColumnName
                    ProcessInMemory(buffer.GetString(_inputColIndex), _outputBuffer);
                }
                catch (Exception e) {
                    bool fireAgain = false;
                    ComponentMetaData.FireError(RUNTIME_GENERIC_ERROR, ComponentMetaData.Name, "An error has occurred: " + e.Message + ". \n" + e.StackTrace, null, 0, out fireAgain);
                    return;
                }
            }

            //IDTSInput100 input = ComponentMetaData.InputCollection.GetObjectByID(inputID);
            //buffer.AddRow();
            if (buffer.EndOfRowset)
                _outputBuffer.SetEndOfRowset();
            
        }

        #region //Process Json Elements
        
       
        private void ProcessInMemory(string jsonData, PipelineBuffer outputBuffer)
        {
            
            bool cancel = false;
            ComponentMetaData.FireInformation(1000, ComponentMetaData.Name, "Loading whole model into memory and deserializing...", null, 0, ref cancel);

            // Navigate to the relative Root.
            try
            {
                // Get all the tokens returned by the XPath string specified
                if (_pathToArray == null)
                    _pathToArray = "";
                // Load the JToken and navigate it to the selected root
                JToken jt = JToken.Parse(jsonData);
                IEnumerable<JToken> els = jt.SelectTokens(_pathToArray);
                int rootEls = els.Count();
                ComponentMetaData.FireInformation(1000, ComponentMetaData.Name, "Array: loaded " + rootEls + " tokens.", null, 0, ref cancel);

                int count = 0;
                // For each root element we got...
                foreach (JToken t in els)
                {
                    if (t.Type == JTokenType.Array)
                    {
                        count += ProcessArray(t as JArray, outputBuffer);
                    }
                    else if (t.Type == JTokenType.Object)
                    {
                        count += ProcessObject(t as JObject, outputBuffer);
                    }
                    else
                    {
                        throw new Exception("Invalid token returned by RootPath query: " + t.Type.ToString());
                    }
                }
                ComponentMetaData.FireInformation(1000, ComponentMetaData.Name, "Succesfully parsed " + count + " tokens.", null, 0, ref cancel);
            }
            catch (Exception ex)
            {
                ComponentMetaData.FireError(RUNTIME_ERROR_MODEL_INVALID, ComponentMetaData.Name, ex.Message + ex.StackTrace + ex.InnerException, null, 0, out cancel);
                throw new Exception("Error occurred: " + ex.Message + ex.StackTrace + ex.InnerException);
            }
            

        }

        private int ProcessObject(JObject obj, PipelineBuffer outputBuffer)
        {
            bool cancel = false;
            ComponentMetaData.FireInformation(1000, ComponentMetaData.Name, "Processing Object...", null, 0, ref cancel);
            // Each objects corresponds to an output row.
            outputBuffer.AddRow();

            // For each column requested from metadata, look for data into the object we parsed
            Parallel.ForEach<IOMapEntry>(_iomap, _opt, delegate(IOMapEntry e)
            {
                // If the user wants to get raw json, we should parse nothing: simply return all the json as a string
                if (e.OutputJsonColumnType == JsonTypes.RawJson)
                {
                    string val = null;
                    var vals = obj.SelectTokens(e.InputFieldPath);
                    if (vals.Count() > 1)
                    {
                        JArray arr = new JArray();
                        foreach (var t in vals)
                        {
                            arr.Add(t);
                        }
                        val = arr.ToString();
                    }
                    else
                    {
                        val = vals.ElementAt(0).ToString();
                    }

                    int colIndex = _outColsMaps[e.OutputColName];
                    outputBuffer[colIndex] = val;
                }
                else
                {
                    // If it's not a json raw type, parse the value.
                    try
                    {
                        object val = obj.SelectToken(e.InputFieldPath);
                        int colIndex = _outColsMaps[e.OutputColName];
                        outputBuffer[colIndex] = val;
                    }
                    catch (Newtonsoft.Json.JsonException ex)
                    {
                        bool fireAgain = false;
                        ComponentMetaData.FireError(ERROR_SELECT_TOKEN, ComponentMetaData.Name, "SelectToken failed. This may be due to an invalid Xpath syntax / member name. However this error still happens if multiple tokens are returned and the value expected is single. Specific error was: " + ex.Message, null, 0, out fireAgain);
                        throw ex;
                    }
                }

            });
            return 1;
        }

        private int ProcessArray(JArray arr, PipelineBuffer buffer)
        {
            bool cancel = false;
            ComponentMetaData.FireInformation(1000, ComponentMetaData.Name, "Processing Array...", null, 0, ref cancel);
            int count = 0;
            foreach (JObject obj in arr)
            {
                // Each objects corresponds to an output row.
                buffer.AddRow();

                // For each column requested from metadata, look for data into the object we parsed
                Parallel.ForEach<IOMapEntry>(_iomap, _opt, delegate(IOMapEntry e)
                {
                    object val = obj.SelectToken(e.InputFieldPath);
                    int colIndex = _outColsMaps[e.OutputColName];
                    buffer[colIndex] = val;
                });
                count++;
            }
            return count;
        }
        
        

        public override IDTSExternalMetadataColumn100 MapInputColumn(int iInputID, int iInputColumnID, int iExternalMetadataColumnID)
        {
            return base.MapInputColumn(iInputID, iInputColumnID, iExternalMetadataColumnID);
        }

        #endregion

    }
    
}