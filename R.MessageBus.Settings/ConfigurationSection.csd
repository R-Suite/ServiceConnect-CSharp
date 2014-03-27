<?xml version="1.0" encoding="utf-8"?>
<configurationSectionModel xmlns:dm0="http://schemas.microsoft.com/VisualStudio/2008/DslTools/Core" dslVersion="1.0.0.0" Id="d0ed9acb-0435-4532-afdd-b5115bc4d562" namespace="BusSettings" xmlSchemaNamespace="BusSettings" xmlns="http://schemas.microsoft.com/dsltools/ConfigurationSectionDesigner">
  <typeDefinitions>
    <externalType name="String" namespace="System" />
    <externalType name="Boolean" namespace="System" />
    <externalType name="Int32" namespace="System" />
    <externalType name="Int64" namespace="System" />
    <externalType name="Single" namespace="System" />
    <externalType name="Double" namespace="System" />
    <externalType name="DateTime" namespace="System" />
    <externalType name="TimeSpan" namespace="System" />
  </typeDefinitions>
  <configurationElements>
    <configurationSection name="BusSettings" codeGenOptions="Singleton, XmlnsProperty" xmlSectionName="BusSettings">
      <elementProperties>
        <elementProperty name="TransportSettings" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="TransportSettings" isReadOnly="false">
          <type>
            <configurationElementCollectionMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/RabbitMq" />
          </type>
        </elementProperty>
        <elementProperty name="PersistanceSettings" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="PersistanceSettings" isReadOnly="false">
          <type>
            <configurationElementCollectionMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/MongoDb" />
          </type>
        </elementProperty>
      </elementProperties>
    </configurationSection>
    <configurationElementCollection name="RabbitMq" xmlItemName="RabbitMq" codeGenOptions="Indexer, AddMethod, RemoveMethod, GetItemMethods">
      <itemType>
        <configurationElementMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/TransportSettings" />
      </itemType>
    </configurationElementCollection>
    <configurationElement name="TransportSettings" namespace="BusConfiguration">
      <attributeProperties>
        <attributeProperty name="EndPoint" isRequired="true" isKey="true" isDefaultCollection="false" xmlName="EndPoint" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="Host" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Host" isReadOnly="false" defaultValue="&quot;localhost&quot;">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="NoAck" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="NoAck" isReadOnly="false" defaultValue="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Boolean" />
          </type>
        </attributeProperty>
        <attributeProperty name="Username" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Username" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="Password" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Password" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
      </attributeProperties>
      <elementProperties>
        <elementProperty name="Queue" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Queue" isReadOnly="false">
          <type>
            <configurationElementMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Queue" />
          </type>
        </elementProperty>
        <elementProperty name="Exchange" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Exchange" isReadOnly="false">
          <type>
            <configurationElementMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Exchange" />
          </type>
        </elementProperty>
        <elementProperty name="Retries" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Retries" isReadOnly="false">
          <type>
            <configurationElementMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Retries" />
          </type>
        </elementProperty>
      </elementProperties>
    </configurationElement>
    <configurationElementCollection name="Arguments" xmlItemName="Argument" codeGenOptions="Indexer, AddMethod, RemoveMethod, GetItemMethods">
      <itemType>
        <configurationElementMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Argument" />
      </itemType>
    </configurationElementCollection>
    <configurationElement name="Argument">
      <attributeProperties>
        <attributeProperty name="Name" isRequired="true" isKey="true" isDefaultCollection="false" xmlName="Name" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="Value" isRequired="true" isKey="false" isDefaultCollection="false" xmlName="value" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
      </attributeProperties>
    </configurationElement>
    <configurationElement name="Queue">
      <attributeProperties>
        <attributeProperty name="Durable" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Durable" isReadOnly="false" defaultValue="true">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Boolean" />
          </type>
        </attributeProperty>
        <attributeProperty name="Exclusive" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Exclusive" isReadOnly="false" defaultValue="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Boolean" />
          </type>
        </attributeProperty>
        <attributeProperty name="AutoDelete" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="AutoDelete" isReadOnly="false" defaultValue="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Boolean" />
          </type>
        </attributeProperty>
        <attributeProperty name="RoutingKey" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="RoutingKey" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="Name" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Name" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
      </attributeProperties>
      <elementProperties>
        <elementProperty name="Arguments" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="arguments" isReadOnly="false">
          <type>
            <configurationElementCollectionMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Arguments" />
          </type>
        </elementProperty>
      </elementProperties>
    </configurationElement>
    <configurationElement name="Exchange">
      <attributeProperties>
        <attributeProperty name="Name" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Name" isReadOnly="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="Type" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Type" isReadOnly="false" defaultValue="&quot;direct&quot;">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="Durable" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Durable" isReadOnly="false" defaultValue="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Boolean" />
          </type>
        </attributeProperty>
        <attributeProperty name="AutoDelete" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="AutoDelete" isReadOnly="false" defaultValue="false">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Boolean" />
          </type>
        </attributeProperty>
      </attributeProperties>
      <elementProperties>
        <elementProperty name="Arguments" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Arguments" isReadOnly="false">
          <type>
            <configurationElementCollectionMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Arguments" />
          </type>
        </elementProperty>
      </elementProperties>
    </configurationElement>
    <configurationElement name="Retries">
      <attributeProperties>
        <attributeProperty name="MaxRetries" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="MaxRetries" isReadOnly="false" defaultValue="3">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Int32" />
          </type>
        </attributeProperty>
        <attributeProperty name="RetryDelay" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="RetryDelay" isReadOnly="false" defaultValue="3000">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Int32" />
          </type>
        </attributeProperty>
      </attributeProperties>
      <elementProperties>
        <elementProperty name="Arguments" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="arguments" isReadOnly="false">
          <type>
            <configurationElementCollectionMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/Arguments" />
          </type>
        </elementProperty>
      </elementProperties>
    </configurationElement>
    <configurationElement name="PersistanceSettings" namespace="BusConfiguration">
      <attributeProperties>
        <attributeProperty name="Database" isRequired="false" isKey="false" isDefaultCollection="false" xmlName="Database" isReadOnly="false" defaultValue="&quot;RMessageBusPersistantStore&quot;">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
        <attributeProperty name="ConnectionString" isRequired="true" isKey="true" isDefaultCollection="false" xmlName="ConnectionString" isReadOnly="false" defaultValue="&quot;mongodb://localhost/&quot;">
          <type>
            <externalTypeMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/String" />
          </type>
        </attributeProperty>
      </attributeProperties>
    </configurationElement>
    <configurationElementCollection name="MongoDb" xmlItemName="MongoDb" codeGenOptions="Indexer, AddMethod, RemoveMethod, GetItemMethods">
      <itemType>
        <configurationElementMoniker name="/d0ed9acb-0435-4532-afdd-b5115bc4d562/PersistanceSettings" />
      </itemType>
    </configurationElementCollection>
  </configurationElements>
  <propertyValidators>
    <validators />
  </propertyValidators>
</configurationSectionModel>